namespace Yomado.Core;

public static class GeometryPolicy
{
    public static DisplayInfo? Owner(PixelRect frame, IEnumerable<DisplayInfo> displays) => displays
        .Where(display => display.Bounds.IsUsable && frame.Intersection(display.Bounds) > 0)
        .OrderByDescending(display => frame.Intersection(display.Bounds))
        .ThenBy(display => display.IsVirtual).ThenBy(display => display.Id, StringComparer.Ordinal).FirstOrDefault();

    public static DisplayInfo? Physical(IEnumerable<DisplayInfo> displays, string? preferred = null) => displays
        .Where(display => !display.IsVirtual && display.Bounds.IsUsable)
        .OrderByDescending(display => display.Id == preferred).ThenByDescending(display => display.IsPrimary)
        .ThenBy(display => display.Id, StringComparer.Ordinal).FirstOrDefault();

    public static PixelRect Place(PixelRect size, DisplayInfo target, PixelRect? relative = null)
    {
        if (!size.IsUsable || !target.Bounds.IsUsable) throw new ArgumentException("窗口或屏幕尺寸无效。");
        var area = target.Bounds.Contains(target.WorkArea, 0) ? target.WorkArea : target.Bounds;
        var width = Math.Min(size.Width, area.Width);
        var height = Math.Min(size.Height, area.Height);
        var x = relative.HasValue ? target.Bounds.X + relative.Value.X : area.X + (area.Width - width) / 2;
        var y = relative.HasValue ? target.Bounds.Y + relative.Value.Y : area.Y + (area.Height - height) / 2;
        return new(Math.Clamp(x, area.X, area.Right - width), Math.Clamp(y, area.Y, area.Bottom - height), width, height);
    }

    public static PixelRect? Relative(PixelRect frame, DisplayInfo source) => source.IsVirtual ? null :
        new PixelRect(frame.X - source.Bounds.X, frame.Y - source.Bounds.Y, frame.Width, frame.Height);

    public static int RightEdge(IEnumerable<DisplayInfo> displays) => displays.Where(d => d.Bounds.IsUsable)
        .Select(d => d.Bounds.Right).DefaultIfEmpty(0).Max();

    public static PixelRect Rescale(PixelRect frame, double from, double to, bool relativePosition = false)
    {
        if (!frame.IsUsable || !double.IsFinite(from) || !double.IsFinite(to) || from < 0.5 || to < 0.5 || from > 8 || to > 8)
            throw new ArgumentException("屏幕缩放比例无效。");
        var ratio = to / from;
        return new(relativePosition ? (int)Math.Round(frame.X * ratio) : frame.X,
            relativePosition ? (int)Math.Round(frame.Y * ratio) : frame.Y,
            Math.Max(1, (int)Math.Round(frame.Width * ratio)), Math.Max(1, (int)Math.Round(frame.Height * ratio)));
    }

    public static bool PlacementVerified(WindowInfo window, PixelRect requested, DisplayInfo target, bool maximize) =>
        Owner(window.Frame, [target])?.Id == target.Id &&
        (window.Minimized ? target.WorkArea.Contains(window.Frame) && window.Frame.Near(requested) :
         maximize ? window.Maximized : !window.Maximized && target.WorkArea.Contains(window.Frame) && window.Frame.Near(requested));
}
