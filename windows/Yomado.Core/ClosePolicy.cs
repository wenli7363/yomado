namespace Yomado.Core;

public sealed record CloseApplication(string Name, int Count);

// Consent is scoped to one display and the exact windows the person saw in
// the prompt. It is never an application-wide or persistent allowance.
public sealed record CloseSnapshot(DisplayInfo Display, IReadOnlyList<WindowInfo> Windows)
{
    public IReadOnlyList<CloseApplication> Applications => Windows
        .GroupBy(window => window.ApplicationPath, StringComparer.OrdinalIgnoreCase)
        .Select(group => new CloseApplication(group.First().ApplicationName, group.Count()))
        .OrderBy(application => application.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    public string Summary => string.Join("\n", Applications.Select(application => $"{application.Name} · {application.Count} 个窗口"));
}

public static class ClosePolicy
{
    public static CloseSnapshot Capture(DisplayInfo display, IReadOnlyList<DisplayInfo> displays,
        IEnumerable<WindowInfo> windows, IEnumerable<WindowOrigin> origins)
    {
        if (!display.IsYomado || !displays.Contains(display))
            throw new InvalidOperationException("来源虚拟屏已变化，已保留屏幕，请刷新后重试。");
        var blockers = windows.Where(window => GeometryPolicy.Owner(window.Frame, displays)?.Id == display.Id)
            .DistinctBy(window => window.Handle).ToArray();
        foreach (var window in blockers)
        {
            if (window.Handle == 0 || window.ProcessId <= 0 || window.ProcessStart <= 0 || window.Generation <= 0 ||
                string.IsNullOrWhiteSpace(window.ApplicationPath) || !window.Frame.IsUsable)
                throw new InvalidOperationException("有窗口的身份尚未核对完成，已保留虚拟屏，请稍后重试。");
            if (origins.Any(origin => Workspace.SameIdentity(origin.Window, window)))
                throw new InvalidOperationException("有已记录原位置的窗口尚未恢复，已保留虚拟屏。");
        }
        return new(display, blockers);
    }

    public static bool CanRemove(CloseSnapshot offered, CloseSnapshot current, bool confirmed)
    {
        if (offered.Display != current.Display || (offered.Windows.Count > 0 && !confirmed)) return false;
        // Windows that have closed or returned to another screen no longer
        // need consent. Reuse, movement, and newly appeared windows do.
        return current.Windows.All(window => offered.Windows.Any(previous =>
            Workspace.SameIdentity(previous, window) && previous.Frame.Near(window.Frame, 0) &&
            previous.Minimized == window.Minimized && previous.IsDialog == window.IsDialog));
    }
}
