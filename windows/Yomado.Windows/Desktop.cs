using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Yomado.Core;

namespace Yomado.Windows;

public sealed class Desktop : IDesktop, IDisposable
{
    private readonly ConcurrentDictionary<nint, long> generations = new();
    private long generation;
    private readonly Native.WinEventCallback callback;
    private readonly nint eventHook;
    public Desktop()
    {
        callback = (_, evt, window, objectId, childId, _, _) =>
        {
            if (window == 0 || objectId != 0 || childId != 0) return;
            if (evt == 0x8001) generations.TryRemove(window, out _);
            else if (generations.ContainsKey(window)) generations[window] = Interlocked.Increment(ref generation);
        };
        eventHook = Native.SetWinEventHook(0x8000, 0x8001, 0, callback, 0, 0, 0);
        if (eventHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法跟踪窗口生命周期。");
    }

    internal static (Native.DisplayPath[] Paths, Native.ModeInfo[] Modes) DisplayConfiguration()
    {
        for (var retry = 0; retry < 4; retry++)
        {
            var error = Native.GetDisplayConfigBufferSizes(Native.QdcActive, out var pathCount, out var modeCount);
            if (error != 0) throw new Win32Exception(error, "无法读取显示配置。");
            var paths = new Native.DisplayPath[pathCount]; var modes = new Native.ModeInfo[modeCount];
            error = Native.QueryDisplayConfig(Native.QdcActive, ref pathCount, paths, ref modeCount, modes, 0);
            if (error == 122) continue;
            if (error != 0) throw new Win32Exception(error, "无法读取显示配置。");
            return (paths.Take((int)pathCount).ToArray(), modes.Take((int)modeCount).ToArray());
        }
        throw new InvalidOperationException("显示配置正在变化，请稍后重试。");
    }

    internal static Native.SourceName SourceName(Native.SourceInfo source)
    {
        var name = new Native.SourceName { Header = new() { Type = 1, Size = (uint)Marshal.SizeOf<Native.SourceName>(), Adapter = source.Adapter, Id = source.Id }, Name = "" };
        var error = Native.GetSourceName(ref name);
        if (error != 0) throw new Win32Exception(error);
        return name;
    }

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        using var dpiContext = new DpiScope();
        for (var retry = 0; retry < 40; retry++)
        {
            var displays = DisplaySnapshot();
            if (displays is not null) return displays;
            Thread.Sleep(25);
        }
        throw new InvalidOperationException("显示配置正在切换，请稍后刷新。");
    }

    private static IReadOnlyList<DisplayInfo>? DisplaySnapshot()
    {
        var config = DisplayConfiguration();
        var targets = new Dictionary<string, Native.TargetName>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in config.Paths)
        {
            var source = SourceName(path.Source);
            var target = new Native.TargetName { Header = new() { Type = 2, Size = (uint)Marshal.SizeOf<Native.TargetName>(), Adapter = path.Target.Adapter, Id = path.Target.Id }, Name = "", Path = "" };
            var error = Native.GetTargetName(ref target);
            if (error != 0) throw new Win32Exception(error);
            if (string.IsNullOrWhiteSpace(target.Path)) return null;
            targets[source.Name] = target;
        }
        var displays = new List<DisplayInfo>();
        var inconsistent = false;
        Native.MonitorCallback enumeration = (handle, _, _, _) =>
        {
            var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>(), Device = "" };
            if (!Native.GetMonitorInfo(handle, ref info)) return true;
            // EnumDisplayMonitors and QueryDisplayConfig can briefly disagree
            // during arrival/departure. An unmatched monitor must never become
            // an invented physical screen in the recovery inventory.
            if (!targets.TryGetValue(info.Device, out var target)) { inconsistent = true; return true; }
            var ours = (target.Path ?? "").Contains("DISPLAY#YMD594D#", StringComparison.OrdinalIgnoreCase);
            uint? slot = ours && target.Connector < DisplayProfile.MaximumDisplays ? target.Connector : null;
            if (ours && (target.Name ?? "").Trim().StartsWith("Yomado ", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(target.Name!.Trim().AsSpan(7), out var ordinal) && ordinal is >= 1 and <= DisplayProfile.MaximumDisplays) slot = ordinal - 1;
            var scale = Native.GetScaleFactorForMonitor(handle, out var percent) == 0 ? percent / 100d :
                Native.GetDpiForMonitor(handle, 0, out var dpi, out _) == 0 ? dpi / 96d : 1;
            displays.Add(new(target.Path!, info.Device,
                string.IsNullOrWhiteSpace(target.Name) ? info.Device : target.Name, info.Monitor.ToPixels(), info.Work.ToPixels(),
                (info.Flags & 1) != 0, ours, slot, scale, target.Technology == 17));
            return true;
        };
        if (!Native.EnumDisplayMonitors(0, 0, enumeration, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var confirmed = DisplayConfiguration();
        if (inconsistent || displays.Count != targets.Count || !config.Paths.SequenceEqual(confirmed.Paths) || !config.Modes.SequenceEqual(confirmed.Modes)) return null;
        return displays.OrderBy(d => d.Bounds.X).ThenBy(d => d.Bounds.Y).ToArray();
    }

    public IReadOnlyList<WindowInfo> GetWindows()
    {
        using var dpiContext = new DpiScope();
        var windows = new List<WindowInfo>();
        Native.WindowCallback enumeration = (handle, _) =>
        {
            if (!Native.IsWindowVisible(handle) || Native.IsIconic(handle) || Native.GetWindow(handle, 4) != 0 ||
                ((long)Native.GetWindowLongPtr(handle, -20) & 0x80) != 0) return true;
            if (Native.DwmGetWindowAttribute(handle, 14, out var cloaked, 4) == 0 && cloaked != 0) return true;
            var window = GetWindow(handle);
            if (window is not null && window.Frame.Width >= 80 && window.Frame.Height >= 64) windows.Add(window);
            return true;
        };
        if (!Native.EnumWindows(enumeration, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return windows;
    }

    public WindowInfo? GetWindow(nint handle)
    {
        using var dpiContext = new DpiScope();
        if (!Native.IsWindow(handle) || Native.GetWindowThreadProcessId(handle, out var pid) == 0 || pid == Environment.ProcessId || pid == 0 ||
            !Native.GetWindowRect(handle, out var rect)) return null;
        string path, name; long start;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            start = process.StartTime.ToUniversalTime().Ticks;
            path = process.MainModule?.FileName ?? "";
            if (path.Length == 0) return null;
            name = Path.GetFileNameWithoutExtension(path);
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (Win32Exception) { path = $"protected-process:{pid}"; name = "受保护的应用"; start = 0; }
        var placement = new Native.Placement { Length = (uint)Marshal.SizeOf<Native.Placement>() };
        var normal = rect.ToPixels();
        if (Native.GetWindowPlacement(handle, ref placement))
        {
            normal = placement.Normal.ToPixels();
            // WINDOWPLACEMENT uses workspace coordinates for ordinary top-level windows.
            var monitor = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>(), Device = "" };
            if (Native.GetMonitorInfo(Native.MonitorFromWindow(handle, 2), ref monitor))
                normal = normal with { X = normal.X + monitor.Work.Left - monitor.Monitor.Left, Y = normal.Y + monitor.Work.Top - monitor.Monitor.Top };
        }
        return new(handle, (int)pid, start, path, name, Native.IsIconic(handle) ? normal : rect.ToPixels(), Native.IsZoomed(handle))
        { NormalFrame = normal, Minimized = Native.IsIconic(handle), Generation = generations.GetOrAdd(handle, _ => Interlocked.Increment(ref generation)) };
    }

    public bool Place(WindowInfo identity, PixelRect frame, bool maximize)
    {
        using var dpiContext = new DpiScope();
        if (!frame.IsUsable || !Workspace.SameIdentity(GetWindow(identity.Handle), identity) || identity.ProcessStart == 0) return false;
        var target = GeometryPolicy.Owner(frame, GetDisplays());
        if (target is null) return false;
        var initialDpi = Native.GetDpiForWindow(identity.Handle);
        var perMonitor = Native.GetAwarenessFromDpiAwarenessContext(Native.GetWindowDpiAwarenessContext(identity.Handle)) == 2;
        bool Apply()
        {
            var placement = new Native.Placement { Length = (uint)Marshal.SizeOf<Native.Placement>() };
            if (!Native.GetWindowPlacement(identity.Handle, ref placement)) return false;
            if (identity.Maximized || maximize || Native.IsIconic(identity.Handle))
            {
                placement.Normal = Native.Rect.From(frame with { X = frame.X - target.WorkArea.X + target.Bounds.X, Y = frame.Y - target.WorkArea.Y + target.Bounds.Y });
                placement.ShowCmd = Native.IsIconic(identity.Handle) ? placement.ShowCmd : maximize ? 3u : 4u;
                if (Native.IsIconic(identity.Handle)) placement.Flags = maximize ? placement.Flags | 2u : placement.Flags & ~2u;
                placement.Flags |= 4; // WPF_ASYNCWINDOWPLACEMENT: never block on another app's input queue.
                return Native.SetWindowPlacement(identity.Handle, placement);
            }
            return Native.SetWindowPos(identity.Handle, 0, frame.X, frame.Y, frame.Width, frame.Height, 0x14 | 0x4000);
        }
        if (!Apply()) return false;
        var transition = -1; var corrected = false; var stable = 0; PixelRect? last = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var actual = GetWindow(identity.Handle);
            if (!Workspace.SameIdentity(actual, identity)) return false;
            var currentDpi = Native.GetDpiForWindow(identity.Handle);
            if (currentDpi != initialDpi && transition < 0) transition = attempt;
            // WM_DPICHANGED may resize an aware application after the first
            // SetWindowPos has already appeared correct. Let the transition
            // settle, then apply the intended physical geometry once.
            if (transition >= 0 && !corrected && attempt - transition >= 4)
            {
                if (!Apply()) return false;
                corrected = true; stable = 0; last = null;
                Thread.Sleep(25); continue;
            }
            if (GeometryPolicy.Owner(actual!.Frame, GetDisplays())?.Id == target.Id &&
                GeometryPolicy.PlacementVerified(actual, frame, target, maximize) &&
                (!perMonitor || actual.Minimized || currentDpi == (uint)Math.Round(target.Scale * 96)) &&
                (transition < 0 || corrected))
            {
                stable = last.HasValue && last.Value.Near(actual.Frame, 0) ? stable + 1 : 1;
                if (attempt >= 4 && stable >= 3) return true;
            }
            else stable = 0;
            last = actual.Frame;
            Thread.Sleep(25);
        }
        return false;
    }

    public static void KeepAwake(bool active) => Native.SetThreadExecutionState(active ? 0x80000003u : 0x80000000u);

    public void Dispose() { Native.UnhookWinEvent(eventHook); GC.KeepAlive(callback); }
    private readonly struct DpiScope : IDisposable
    {
        private readonly nint previous;
        public DpiScope()
        {
            previous = Native.SetThreadDpiAwarenessContext(-4);
            if (previous == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法建立物理像素坐标上下文。");
        }
        public void Dispose() => Native.SetThreadDpiAwarenessContext(previous);
    }
}
