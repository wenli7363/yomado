namespace Yomado.Core;

public readonly record struct PixelPoint(int X, int Y);

// Only entry from an ordinary monitor into a Yomado monitor is blocked. The
// desktop remains extended so explicitly moved windows and Agent input work.
public static class CursorBoundaryPolicy
{
    public static PixelPoint Constrain(PixelPoint previous, PixelPoint requested, IReadOnlyList<DisplayInfo> displays,
        bool allowVirtualInput = false)
    {
        // Synthetic positioning by a screenshot tool is still ordinary user
        // interaction. Only an explicitly granted lease may bypass this edge.
        if (allowVirtualInput) return requested;
        var source = At(previous, displays);
        if (source is null || source.IsYomado || At(requested, displays) is not { IsYomado: true }) return requested;
        return new(Math.Clamp(requested.X, source.Bounds.X, source.Bounds.Right - 1),
            Math.Clamp(requested.Y, source.Bounds.Y, source.Bounds.Bottom - 1));
    }

    internal static DisplayInfo? At(PixelPoint point, IReadOnlyList<DisplayInfo> displays)
    {
        foreach (var display in displays)
            if (display.Bounds.IsUsable && point.X >= display.Bounds.X && point.X < display.Bounds.Right &&
                point.Y >= display.Bounds.Y && point.Y < display.Bounds.Bottom) return display;
        return null;
    }
}

// Uses a last safe ordinary-monitor anchor rather than trusting the preceding
// callback: screenshot tools can reposition the cursor without that callback.
public sealed class CursorBoundaryTracker
{
    private DisplayInfo[] topology = [];
    private PixelPoint? ordinaryPoint;

    public PixelPoint Observe(PixelPoint requested, IReadOnlyList<DisplayInfo> displays, bool allowVirtualInput = false)
    {
        if (!topology.SequenceEqual(displays))
        {
            topology = displays.ToArray();
            ordinaryPoint = null;
        }
        if (allowVirtualInput)
        {
            Suspend();
            return requested;
        }
        if (CursorBoundaryPolicy.At(requested, displays) is { IsYomado: false })
        {
            ordinaryPoint = requested;
            return requested;
        }
        return ordinaryPoint.HasValue ? CursorBoundaryPolicy.Constrain(ordinaryPoint.Value, requested, displays) : requested;
    }

    // A lease may leave the cursor on a virtual monitor. Do not pull it back
    // after expiry; resume protection once an ordinary monitor is observed.
    public void Suspend() => ordinaryPoint = null;
}
