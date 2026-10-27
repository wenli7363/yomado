using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Yomado.Core;
using Yomado.Windows;

namespace Yomado.App;

internal static class InputLeaseNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    internal delegate void WinEventCallback(nint hook, uint evt, nint window, int objectID, int childID, uint thread, uint time);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processID);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint minimum, uint maximum, nint module,
        WinEventCallback callback, uint processID, uint threadID, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint window,
        nint after, int x, int y, int width, int height, uint flags);
    internal static void MakeNonActivating(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | (nint)0x08000080); // NOACTIVATE | TOOLWINDOW
        HwndSource.FromHwnd(handle)?.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (message == 0x21) { handled = true; return (nint)3; } // MA_NOACTIVATE: first click still reaches the button.
            if (message == 0x112 && ((long)wParam & 0xfff0) is 0xf010 or 0xf020 or 0xf030)
            { handled = true; return 0; } // Keep Move/Minimize/Maximize from hiding the physical-screen stop control.
            return 0;
        });
    }

    internal static void Position(Window window, bool corner)
    {
        using var desktop = new Desktop();
        var displays = desktop.GetDisplays();
        var physical = GeometryPolicy.Physical(displays) ?? throw new InvalidOperationException("请连接实体显示器后申请键鼠借用。");
        window.UpdateLayout();
        var handle = new WindowInteropHelper(window).Handle;
        var scale = physical.Scale;
        var width = (int)Math.Ceiling(window.ActualWidth * scale);
        var height = (int)Math.Ceiling(window.ActualHeight * scale);
        if (width < 100 || height < 80 || width > physical.WorkArea.Width - 24 || height > physical.WorkArea.Height - 24)
            throw new InvalidOperationException("实体显示器空间不足，无法完整显示键鼠借用提示和停止按钮。");
        var x = corner ? physical.WorkArea.Right - width - 12 : physical.WorkArea.X + (physical.WorkArea.Width - width) / 2;
        var y = corner ? physical.WorkArea.Y + 12 : physical.WorkArea.Y + Math.Max(12, (physical.WorkArea.Height - height) / 5);
        if (!SetWindowPos(handle, (nint)(-1), x, y, width, height, 0x10 | 0x40))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static bool VisibleOnPhysical(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (!IsWindowVisible(handle) || IsIconic(handle) || !GetWindowRect(handle, out var rect)) return false;
        using var desktop = new Desktop();
        var physical = GeometryPolicy.Physical(desktop.GetDisplays());
        return physical is not null && physical.WorkArea.Contains(new(rect.Left, rect.Top,
            rect.Right - rect.Left, rect.Bottom - rect.Top), 0);
    }
}

// The destroy hook distinguishes handle reuse even inside the same process.
// If capture/identity evidence is incomplete, normal completion skips restore.
internal sealed class InputLeaseControlsSnapshot : IDisposable
{
    private readonly nint window;
    private readonly uint processID, threadID;
    private readonly long processStart;
    private readonly InputLeaseNative.Point cursor;
    private readonly bool hasCursor;
    private readonly DisplayInfo? cursorDisplay;
    private readonly InputLeaseNative.WinEventCallback callback;
    private nint hook;
    private bool destroyed;

    private InputLeaseControlsSnapshot()
    {
        // Subscribe before observing HWND/PID. A destroy during capture must
        // invalidate the snapshot even when that same process reuses the HWND.
        callback = (_, _, observed, objectID, childID, _, _) =>
        { if (observed == window && objectID == 0 && childID == 0) destroyed = true; };
        hook = InputLeaseNative.SetWinEventHook(0x8001, 0x8001, 0, callback, 0, 0, 0);
        try
        {
        window = InputLeaseNative.GetForegroundWindow();
        threadID = InputLeaseNative.GetWindowThreadProcessId(window, out processID);
        if (hook == 0 || window == 0 || threadID == 0 || processID == 0)
            throw new InvalidOperationException("无法核对原前台窗口。");
        using var process = Process.GetProcessById(checked((int)processID));
        processStart = process.StartTime.ToUniversalTime().Ticks;
        hasCursor = InputLeaseNative.GetCursorPos(out cursor);
        if (hasCursor)
        {
            try
            {
                using var desktop = new Desktop();
                cursorDisplay = GeometryPolicy.Owner(new PixelRect(cursor.X, cursor.Y, 1, 1), desktop.GetDisplays());
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        if (!StillSame()) destroyed = true;
        }
        catch { Dispose(); throw; }
    }

    internal static InputLeaseControlsSnapshot? Capture()
    {
        try { return new(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException) { return null; }
    }

    private bool StillSame()
    {
        if (destroyed || !InputLeaseNative.IsWindow(window) || !InputLeaseNative.IsWindowVisible(window) || InputLeaseNative.IsIconic(window)) return false;
        if (InputLeaseNative.GetWindowThreadProcessId(window, out var actual) != threadID || actual != processID) return false;
        try
        {
            using var process = Process.GetProcessById(checked((int)processID));
            return process.StartTime.ToUniversalTime().Ticks == processStart;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or OverflowException) { return false; }
    }

    internal void Restore()
    {
        if (hook == 0 || !StillSame()) return;
        // A missing display must not turn an old point into an unrelated screen.
        if (hasCursor && cursorDisplay is not null)
        {
            using var desktop = new Desktop();
            if (desktop.GetDisplays().Any(d => d.Id == cursorDisplay.Id && d.Bounds == cursorDisplay.Bounds && Math.Abs(d.Scale - cursorDisplay.Scale) < 0.01))
                InputLeaseNative.SetCursorPos(cursor.X, cursor.Y);
        }
        if (StillSame()) InputLeaseNative.SetForegroundWindow(window);
    }

    public void Dispose()
    {
        if (hook != 0) InputLeaseNative.UnhookWinEvent(hook);
        hook = 0; destroyed = true;
    }
}
