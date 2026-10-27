using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Yomado.Core;

namespace Yomado.Windows;

// Use an OS boundary where one rectangle safely includes all ordinary screens;
// retain event and position reconciliation for more complex arrangements.
public sealed class VirtualDisplayPointerBoundary : IDisposable
{
    private readonly Thread thread;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PointerNative.HookCallback callback;
    private readonly CursorBoundaryTracker tracker = new();
    private readonly PointerClipManager clipManager = new(new NativePointerClipBackend());
    private readonly ConcurrentQueue<TaskCompletionSource<bool>> releaseRequests = new();
    private PointerClipState clipState = new(false, null, null, null);
    private DisplayInfo[] displays = [];
    private uint threadId;
    private nint hook;
    private bool correcting;
    private long allowVirtualUntil;
    private int disposed;
    private string? error;
    private long observedMoves, injectedMoves, constrainedMoves, correctionFailures, recoveryMoves;

    public VirtualDisplayPointerBoundary()
    {
        callback = MouseMove;
        thread = new Thread(Run) { IsBackground = true, Name = "Yomado pointer boundary" };
        thread.Start();
        ready.Task.GetAwaiter().GetResult();
    }

    public bool IsActive => Interlocked.CompareExchange(ref hook, 0, 0) != 0;
    public string? Error => Volatile.Read(ref error);

    // In-memory counters exclude our own synchronous correction callbacks.
    // Reading diagnostics never performs mouse input or desktop capture.
    public PointerBoundaryStatus GetStatus()
    {
        var clip = Volatile.Read(ref clipState);
        return new(IsActive, Error, LeaseActive, Volatile.Read(ref displays).Length,
            Interlocked.Read(ref observedMoves), Interlocked.Read(ref injectedMoves),
            Interlocked.Read(ref constrainedMoves), Interlocked.Read(ref correctionFailures), Interlocked.Read(ref recoveryMoves),
            clip.Applied, clip.Bounds, clip.Current, clip.Error);
    }

    private bool LeaseActive => Stopwatch.GetTimestamp() < Interlocked.Read(ref allowVirtualUntil);

    // A monotonic deadline keeps the exception bounded even if the UI thread
    // stalls while an explicitly granted input lease is active.
    public void AllowVirtualInputFor(TimeSpan remaining)
    {
        var seconds = Math.Clamp(remaining.TotalSeconds, 0, 600);
        var deadline = seconds <= 0 ? 0 : Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
        Interlocked.Exchange(ref allowVirtualUntil, deadline);
        if (seconds > 0)
        {
            try
            {
                if (Volatile.Read(ref disposed) != 0) throw new InvalidOperationException("鼠标边界服务已退出。");
                if (Thread.CurrentThread == thread)
                {
                    tracker.Suspend();
                    ReleaseClip(Volatile.Read(ref displays));
                    if (clipManager.State.Applied) throw new InvalidOperationException("尚未解除 yomado 的光标限制，未授予键鼠借用。");
                }
                else if (thread.IsAlive)
                {
                    var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    releaseRequests.Enqueue(released);
                    if (!PointerNative.PostThreadMessage(threadId, 0x8002, 0, 0) ||
                        !released.Task.WaitAsync(TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult())
                        throw new InvalidOperationException("尚未解除 yomado 的光标限制，未授予键鼠借用。");
                }
                else if (Volatile.Read(ref clipState).Applied)
                    throw new InvalidOperationException("鼠标边界服务不可用，尚未确认光标限制已解除。");
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                Interlocked.Exchange(ref allowVirtualUntil, 0);
                throw new InvalidOperationException("无法及时解除 yomado 的光标限制，未授予键鼠借用。", ex);
            }
        }
        if (threadId != 0 && Volatile.Read(ref disposed) == 0) PointerNative.PostThreadMessage(threadId, 0x8001, 0, 0);
    }

    public void UpdateDisplays(IReadOnlyList<DisplayInfo> snapshot)
    {
        var next = snapshot.ToArray();
        try { DisplayTopology.RequireExtended(next); }
        catch (InvalidOperationException) { next = []; }
        Volatile.Write(ref displays, next);
        if (threadId != 0 && Volatile.Read(ref disposed) == 0) PointerNative.PostThreadMessage(threadId, 0x8001, 0, 0);
    }

    private void Run()
    {
        nint oldDpi = 0;
        nuint timer = 0;
        try
        {
            threadId = PointerNative.GetCurrentThreadId();
            oldDpi = Native.SetThreadDpiAwarenessContext(-4);
            if (oldDpi == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法建立鼠标边界的物理像素坐标上下文。");
            // Create the queue before publishing readiness so disposal can post
            // WM_QUIT even before the first hardware mouse movement.
            PointerNative.PeekMessage(out _, 0, 0, 0, 0);
            hook = PointerNative.SetWindowsHookEx(14, callback, PointerNative.GetModuleHandle(null), 0);
            if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启用虚拟屏鼠标边界。");
            timer = PointerNative.SetTimer(0, 0, 16, 0);
            if (timer == 0) Volatile.Write(ref error, "鼠标边界已启用，但光标位置复核计时器不可用。");
            ready.TrySetResult();
            while (PointerNative.GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Id == 0x8002)
                {
                    tracker.Suspend();
                    ReleaseClip(Volatile.Read(ref displays));
                    while (releaseRequests.TryDequeue(out var release)) release.TrySetResult(!clipManager.State.Applied);
                    continue;
                }
                if ((message.Id == 0x0113 && message.WParam == timer) || message.Id == 0x8001)
                {
                    PollPointer();
                    continue;
                }
                PointerNative.TranslateMessage(in message);
                PointerNative.DispatchMessage(in message);
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref error, ex.Message);
            ready.TrySetResult(); // Pointer protection must not prevent window recovery.
        }
        finally
        {
            if (timer != 0) PointerNative.KillTimer(0, timer);
            ReleaseClip(Volatile.Read(ref displays));
            while (releaseRequests.TryDequeue(out var release)) release.TrySetResult(false);
            if (hook != 0) PointerNative.UnhookWindowsHookEx(hook);
            hook = 0;
            if (oldDpi != 0) Native.SetThreadDpiAwarenessContext(oldDpi);
            GC.KeepAlive(callback);
        }
    }

    private nint MouseMove(int code, nint message, nint data)
    {
        if (code < 0 || message != 0x0200 || correcting || Volatile.Read(ref disposed) != 0)
            return PointerNative.CallNextHookEx(hook, code, message, data);
        try
        {
            var move = Marshal.PtrToStructure<PointerNative.MouseData>(data);
            Interlocked.Increment(ref observedMoves);
            if ((move.Flags & 1) != 0) Interlocked.Increment(ref injectedMoves);
            var requested = new PixelPoint(move.Point.X, move.Point.Y);
            var snapshot = Volatile.Read(ref displays);
            var constrained = tracker.Observe(requested, snapshot, LeaseActive);
            if (constrained != requested)
            {
                // Let the OS process motion against its installed rectangle;
                // post-event correction here can lose the vertical component
                // already mapped toward a shorter neighbouring monitor.
                if (Volatile.Read(ref clipState).Applied)
                    return PointerNative.CallNextHookEx(hook, code, message, data);
                Interlocked.Increment(ref constrainedMoves);
                if (Correct(constrained, snapshot)) return 1;
            }
        }
        catch (Exception) { /* An unavailable desktop must never trap input. */ }
        return PointerNative.CallNextHookEx(hook, code, message, data);
    }

    private void PollPointer()
    {
        if (correcting || Volatile.Read(ref disposed) != 0) return;
        var snapshot = Volatile.Read(ref displays);
        if (!snapshot.Any(display => display.IsYomado) || LeaseActive)
        {
            ReleaseClip(snapshot);
            tracker.Suspend();
            return;
        }
        try
        {
            UpdateClip(snapshot);
            if (!PointerNative.GetCursorPos(out var point)) return;
            var actual = new PixelPoint(point.X, point.Y);
            var constrained = tracker.Observe(actual, snapshot);
            if (constrained != actual && Correct(constrained, snapshot) && PointerNative.GetCursorPos(out var confirmed))
            {
                if (confirmed.X == constrained.X && confirmed.Y == constrained.Y) Interlocked.Increment(ref recoveryMoves);
                else Interlocked.Increment(ref correctionFailures);
            }
        }
        catch (Exception) { /* Desktop transitions must leave input available. */ }
    }

    private void UpdateClip(DisplayInfo[] snapshot)
    {
        if (LeaseActive || Volatile.Read(ref disposed) != 0 || !ReferenceEquals(snapshot, Volatile.Read(ref displays))) return;
        clipManager.Update(snapshot);
        Volatile.Write(ref clipState, clipManager.State);
    }

    private void ReleaseClip(DisplayInfo[] snapshot)
    {
        clipManager.Release(snapshot);
        Volatile.Write(ref clipState, clipManager.State);
    }

    private sealed class NativePointerClipBackend : IPointerClipBackend
    {
        public string? Error { get; private set; }
        public PixelRect? Read()
        {
            if (PointerNative.GetClipCursor(out var rectangle)) return rectangle.ToPixels();
            Error = new Win32Exception(Marshal.GetLastWin32Error(), "无法读取系统光标限制。").Message;
            return null;
        }
        public bool Apply(PixelRect? rectangle)
        {
            bool success;
            if (rectangle.HasValue)
            {
                var native = Native.Rect.From(rectangle.Value);
                success = PointerNative.ClipCursor(in native);
            }
            else success = PointerNative.ReleaseClipCursor(0);
            if (!success) Error = new Win32Exception(Marshal.GetLastWin32Error(), "无法更新系统光标限制。").Message;
            return success;
        }
    }

    private bool Correct(PixelPoint constrained, DisplayInfo[] snapshot)
    {
        if (Volatile.Read(ref disposed) != 0 || !ReferenceEquals(snapshot, Volatile.Read(ref displays))) return false;
        if (LeaseActive) { tracker.Suspend(); return false; }
        correcting = true;
        try
        {
            if (PointerNative.SetCursorPos(constrained.X, constrained.Y)) return true;
            Interlocked.Increment(ref correctionFailures);
        }
        catch (Exception) { Interlocked.Increment(ref correctionFailures); }
        finally { correcting = false; }
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (thread.IsAlive)
        {
            PointerNative.PostThreadMessage(threadId, 0x0012, 0, 0);
            if (Thread.CurrentThread != thread) thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}

internal static class PointerNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseData
    { public Point Point; public uint Mouse, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct Message
    { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }
    internal delegate nint HookCallback(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(int id, HookCallback callback, nint module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClipCursor(out Native.Rect rectangle);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClipCursor(in Native.Rect rectangle);
    [DllImport("user32.dll", EntryPoint = "ClipCursor", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ReleaseClipCursor(nint rectangle);
    [DllImport("user32.dll")] internal static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TranslateMessage(in Message message);
    [DllImport("user32.dll")] internal static extern nint DispatchMessage(in Message message);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nuint SetTimer(nint window, nuint id, uint interval, nint callback);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool KillTimer(nint window, nuint id);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
}
