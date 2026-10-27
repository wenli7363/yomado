namespace Yomado.Core;

public sealed class Workspace(IDesktop desktop)
{
    private readonly Dictionary<nint, WindowOrigin> origins = [];
    public IReadOnlyCollection<WindowOrigin> Origins => origins.Values;
    public Action<WindowOrigin>? CheckpointBeforeMove { get; set; }

    public IReadOnlyList<WindowInfo> WindowsOn(string displayId)
    {
        var displays = desktop.GetDisplays();
        return desktop.GetWindows().Where(window => GeometryPolicy.Owner(window.Frame, displays)?.Id == displayId).ToArray();
    }

    public void MoveApplication(string path, string? sourceId, string targetId)
    {
        var displays = desktop.GetDisplays();
        var target = displays.SingleOrDefault(d => d.Id == targetId && d.IsYomado)
            ?? throw new InvalidOperationException("目标虚拟屏不可用，请刷新后重试。");
        var windows = desktop.GetWindows().Where(w => string.Equals(w.ApplicationPath, path, StringComparison.OrdinalIgnoreCase) &&
            (sourceId is null || GeometryPolicy.Owner(w.Frame, displays)?.Id == sourceId) &&
            GeometryPolicy.Owner(w.Frame, displays)?.Id != targetId).ToArray();
        if (windows.Length == 0) throw new InvalidOperationException("请先打开应用的普通窗口；最小化、隐藏窗口不会移入。");
        foreach (var window in windows)
        {
            var source = GeometryPolicy.Owner(window.Frame, displays) ?? throw new InvalidOperationException("无法确定窗口来源屏幕。");
            var current = desktop.GetWindow(window.Handle);
            if (!SameIdentity(current, window) || !current!.Frame.Near(window.Frame))
                throw new InvalidOperationException("窗口已变化，已停止后续移动；已移动的窗口可以返回。");
            if (origins.TryGetValue(window.Handle, out var old) && !SameIdentity(old.Window, window)) origins.Remove(window.Handle);
            if (!origins.TryGetValue(window.Handle, out var origin))
                origin = new(window, source.IsVirtual ? null : source.Id, window.NormalFrame,
                    GeometryPolicy.Relative(window.NormalFrame, source), window.Maximized, source.Id, source.Scale);
            var pending = origin with { LastDisplayId = targetId };
            CheckpointBeforeMove?.Invoke(pending);
            origins[window.Handle] = pending; // checkpoint before the first write
            var size = GeometryPolicy.Rescale(window.Maximized ? window.NormalFrame : window.Frame, source.Scale, target.Scale);
            var destination = GeometryPolicy.Place(size, target);
            if (!PlaceAndVerify(window, destination, target, window.Maximized))
            {
                if (PlaceAndVerify(window, window.Maximized ? window.NormalFrame : window.Frame, source, window.Maximized))
                {
                    if (old is null) origins.Remove(window.Handle);
                    else origins[window.Handle] = old;
                }
                throw new InvalidOperationException("移屏未获验证，已停止后续移动并保留必要的恢复记录。");
            }
        }
    }

    public void RestoreApplication(string path, string sourceId, string? physicalTargetId = null)
    {
        var displays = desktop.GetDisplays();
        if (!displays.Any(d => d.Id == sourceId && d.IsYomado)) throw new InvalidOperationException("来源虚拟屏已不可用。");
        var candidates = WindowsOn(sourceId).Where(w => string.Equals(w.ApplicationPath, path, StringComparison.OrdinalIgnoreCase)).ToList();
        // A tracked hidden/minimized window must also return before display removal.
        foreach (var record in origins.Values.Where(o => o.LastDisplayId == sourceId &&
                     string.Equals(o.Window.ApplicationPath, path, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            var live = desktop.GetWindow(record.Window.Handle);
            if (!SameIdentity(live, record.Window)) { origins.Remove(record.Window.Handle); continue; }
            if (candidates.All(w => w.Handle != live!.Handle)) candidates.Add(live!);
        }
        foreach (var window in candidates)
        {
            origins.TryGetValue(window.Handle, out var origin);
            if (origin is not null && !SameIdentity(origin.Window, window)) { origins.Remove(window.Handle); origin = null; }
            var target = physicalTargetId is null ? GeometryPolicy.Physical(displays, origin?.PhysicalDisplayId) :
                displays.SingleOrDefault(d => d.Id == physicalTargetId && !d.IsVirtual);
            if (target is null) throw new InvalidOperationException("没有可用的目标实体屏；虚拟屏和恢复记录已保留。");
            var sourceScale = GeometryPolicy.Owner(window.Frame, displays)?.Scale ?? target.Scale;
            var relative = origin?.PhysicalDisplayId == target.Id && origin.RelativeFrame.HasValue ?
                GeometryPolicy.Rescale(origin.RelativeFrame.Value, origin.OriginalScale, target.Scale, true) : (PixelRect?)null;
            var size = GeometryPolicy.Rescale(origin?.OriginalFrame ?? window.NormalFrame, origin?.OriginalScale ?? sourceScale, target.Scale);
            var destination = GeometryPolicy.Place(size, target, relative);
            if (!PlaceAndVerify(window, destination, target, origin?.WasMaximized ?? window.Maximized))
                throw new InvalidOperationException("尚未确认窗口已返回实体屏；请关闭弹窗后重试，虚拟屏已保留。");
            origins.Remove(window.Handle);
        }
    }

    public void RestoreDisplay(string displayId)
    {
        var paths = WindowsOn(displayId).Select(w => w.ApplicationPath).Concat(origins.Values
            .Where(o => o.LastDisplayId == displayId).Select(o => o.Window.ApplicationPath)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.ToArray()) RestoreApplication(path, displayId);
        if (WindowsOn(displayId).Count != 0 || origins.Values.Any(o => o.LastDisplayId == displayId))
            throw new InvalidOperationException("屏幕中仍有待恢复窗口，尚未关闭虚拟屏。");
    }

    public void RefreshOrigins()
    {
        var displays = desktop.GetDisplays();
        foreach (var record in origins.Values.ToArray())
        {
            var live = desktop.GetWindow(record.Window.Handle);
            if (!SameIdentity(live, record.Window)) { origins.Remove(record.Window.Handle); continue; }
            var owner = GeometryPolicy.Owner(live!.Frame, displays);
            if (owner is { IsVirtual: false }) origins.Remove(live.Handle);
        }
    }

    private bool PlaceAndVerify(WindowInfo window, PixelRect frame, DisplayInfo target, bool maximize)
    {
        var liveTarget = desktop.GetDisplays().SingleOrDefault(d => d.Id == target.Id);
        if (liveTarget is null || !liveTarget.Bounds.Near(target.Bounds) || !desktop.Place(window, frame, maximize)) return false;
        var actual = desktop.GetWindow(window.Handle);
        return SameIdentity(actual, window) && GeometryPolicy.Owner(actual!.Frame, desktop.GetDisplays())?.Id == target.Id &&
            GeometryPolicy.PlacementVerified(actual, frame, target, maximize);
    }

    public static bool SameIdentity(WindowInfo? a, WindowInfo b) => a is not null && a.Handle == b.Handle &&
        a.ProcessId == b.ProcessId && a.ProcessStart == b.ProcessStart && a.Generation == b.Generation &&
        string.Equals(a.ApplicationPath, b.ApplicationPath, StringComparison.OrdinalIgnoreCase);
}
