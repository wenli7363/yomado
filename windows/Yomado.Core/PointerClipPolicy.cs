namespace Yomado.Core;

public static class PointerClipPolicy
{
    // A single rectangle can preserve all ordinary monitors only when its
    // bounding rectangle includes no part of a Yomado monitor.
    public static PixelRect? Plan(IReadOnlyList<DisplayInfo> displays)
    {
        try { DisplayTopology.RequireExtended(displays); }
        catch (InvalidOperationException) { return null; }
        if (!displays.Any(display => display.IsYomado)) return null;
        var ordinary = DesktopBounds(displays.Where(display => !display.IsYomado));
        return ordinary.HasValue && displays.Where(display => display.IsYomado)
            .All(display => display.Bounds.Intersection(ordinary.Value) == 0) ? ordinary : null;
    }

    public static PixelRect? DesktopBounds(IEnumerable<DisplayInfo> displays)
    {
        var rectangles = displays.Select(display => display.Bounds).Where(rectangle => rectangle.IsUsable).ToArray();
        if (rectangles.Length == 0) return null;
        var x = rectangles.Min(rectangle => rectangle.X);
        var y = rectangles.Min(rectangle => rectangle.Y);
        var bounds = new PixelRect(x, y, rectangles.Max(rectangle => rectangle.Right) - x,
            rectangles.Max(rectangle => rectangle.Bottom) - y);
        return bounds.IsUsable ? bounds : null;
    }

    // Existing constraints belong to the current foreground application.
    // Intersect them rather than enlarging a screenshot/game's rectangle.
    public static PixelRect? Intersect(PixelRect current, PixelRect allowed)
    {
        if (!current.IsUsable || !allowed.IsUsable) return null;
        var x = Math.Max(current.X, allowed.X);
        var y = Math.Max(current.Y, allowed.Y);
        var rectangle = new PixelRect(x, y, Math.Min(current.Right, allowed.Right) - x,
            Math.Min(current.Bottom, allowed.Bottom) - y);
        return rectangle.IsUsable ? rectangle : null;
    }
}

public sealed record PointerClipRestoration(PixelRect? Bounds);

// Win32 has no clip-owner handle. Only restore a rectangle that still exactly
// matches our write; another application's change immediately loses our claim.
public sealed class PointerClipOwnership
{
    private sealed record Claim(PixelRect Previous, PixelRect Applied, PixelRect Desktop, DisplayInfo[] Topology);
    private Claim? claim;
    public PixelRect? Applied => claim?.Applied;

    public void Acquire(PixelRect previous, PixelRect applied, PixelRect desktop, IReadOnlyList<DisplayInfo> topology) =>
        claim = previous == applied ? null : new(previous, applied, desktop, topology.ToArray());

    public void Observe(PixelRect current)
    {
        if (claim is not null && current != claim.Applied) claim = null;
    }

    public bool MatchesTopology(IReadOnlyList<DisplayInfo> topology) => claim is not null && claim.Topology.SequenceEqual(topology);

    public PointerClipRestoration? Restoration(PixelRect current, IReadOnlyList<DisplayInfo> topology)
    {
        Observe(current);
        if (claim is null) return null;
        // NULL releases to today's desktop; an old full-desktop rectangle or
        // old custom coordinates must not survive a monitor hotplug.
        return new(claim.Previous == claim.Desktop || !MatchesTopology(topology) ? null : claim.Previous);
    }

    public void Forget() => claim = null;
}

public interface IPointerClipBackend
{
    PixelRect? Read();
    bool Apply(PixelRect? rectangle);
    string? Error { get; }
}

public sealed record PointerClipState(bool Applied, PixelRect? Bounds, PixelRect? Current, string? Error);

// All native writes are behind one ownership state machine so tests can cover
// another application changing its clip, lease release, and monitor hotplug.
public sealed class PointerClipManager(IPointerClipBackend backend)
{
    private readonly PointerClipOwnership ownership = new();
    private string? error;
    public PointerClipState State { get; private set; } = new(false, null, null, null);

    public void Update(IReadOnlyList<DisplayInfo> topology)
    {
        var allowed = PointerClipPolicy.Plan(topology);
        if (!allowed.HasValue) { Release(topology); return; }
        var current = Read();
        if (!current.HasValue) return;
        ownership.Observe(current.Value);
        if (ownership.Applied.HasValue && !ownership.MatchesTopology(topology))
        {
            Release(topology, current);
            if (ownership.Applied.HasValue) return;
            current = Read();
            if (!current.HasValue) return;
        }
        var desired = PointerClipPolicy.Intersect(current.Value, allowed.Value);
        if (desired.HasValue && desired.Value != current.Value && PointerClipPolicy.DesktopBounds(topology) is { } desktop)
        {
            if (Apply(desired))
            {
                ownership.Acquire(current.Value, desired.Value, desktop, topology);
                current = Read();
                if (current.HasValue) ownership.Observe(current.Value);
            }
        }
        Publish(current);
    }

    public void Release(IReadOnlyList<DisplayInfo> topology, PixelRect? current = null)
    {
        // Empty/probe snapshots with no previous write cause zero backend calls.
        if (!ownership.Applied.HasValue) return;
        current ??= Read();
        if (!current.HasValue) return;
        var restore = ownership.Restoration(current.Value, topology);
        if (restore is not null && Apply(restore.Bounds))
        {
            ownership.Forget();
            current = Read();
        }
        Publish(current);
    }

    private PixelRect? Read()
    {
        var current = backend.Read();
        if (!current.HasValue)
        {
            error = backend.Error ?? "无法读取系统光标限制。";
            Publish(null);
        }
        return current;
    }

    private bool Apply(PixelRect? rectangle)
    {
        if (backend.Apply(rectangle)) { error = null; return true; }
        error = backend.Error ?? "无法更新系统光标限制。";
        return false;
    }

    private void Publish(PixelRect? current) => State = new(ownership.Applied.HasValue, ownership.Applied, current, error);
}
