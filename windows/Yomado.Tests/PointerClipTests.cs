using Yomado.Core;

internal static class PointerClipTests
{
    public static void Register(Action<string, Action> test)
    {
        var physical = new DisplayInfo("physical", "physical", "Main", new(0, 0, 3840, 2160), new(0, 0, 3840, 2100), true, false, Scale: 2.25);
        var virtualScreen = new DisplayInfo("virtual", "virtual", "Yomado", new(3840, 0, 1920, 1080), new(3840, 0, 1920, 1040), false, true);
        var desktop = new PixelRect(0, 0, 5760, 2160);
        static void Check(bool condition) { if (!condition) throw new Exception("Pointer clip policy assertion failed."); }
        test("OS clip preserves all 2160 physical rows with a shorter mixed-DPI virtual screen", () =>
            Check(PointerClipPolicy.Plan([physical, virtualScreen]) == physical.Bounds));
        test("OS clip includes negative-coordinate ordinary monitors", () =>
        {
            var left = physical with { Id = "left", IsPrimary = false, Bounds = new(-1920, -200, 1920, 1080) };
            Check(PointerClipPolicy.Plan([physical, virtualScreen, left]) == new PixelRect(-1920, -200, 5760, 2360));
        });
        test("OS clip preserves another vendor virtual monitor as an ordinary screen", () =>
        {
            var external = physical with { Id = "external", IsPrimary = false, IsExternalVirtual = true, Bounds = new(-1920, 0, 1920, 1080) };
            Check(PointerClipPolicy.Plan([physical, virtualScreen, external]) == new PixelRect(-1920, 0, 5760, 2160));
        });
        test("Yomado inside an ordinary-monitor bounding hole disables rectangular clipping", () =>
        {
            var topLeft = physical with { Bounds = new(0, 0, 1920, 1080) };
            var bottomRight = physical with { Id = "second", IsPrimary = false, Bounds = new(1920, 1080, 1920, 1080) };
            var hole = virtualScreen with { Bounds = new(1920, 0, 1920, 1080) };
            Check(PointerClipPolicy.Plan([topLeft, bottomRight, hole]) is null);
        });
        test("invalid or unmanaged topology never installs a rectangular clip", () =>
        {
            Check(PointerClipPolicy.Plan([]) is null);
            Check(PointerClipPolicy.Plan([physical]) is null);
            Check(PointerClipPolicy.Plan([physical, virtualScreen with { Bounds = physical.Bounds }]) is null);
        });
        test("another app narrower screenshot clip is never enlarged", () =>
        {
            var half = new PixelRect(100, 100, 3000, 900);
            Check(PointerClipPolicy.Intersect(half, physical.Bounds) == half);
        });
        test("existing clip reaching virtual is intersected rather than replaced", () =>
        {
            Check(PointerClipPolicy.Intersect(new(3000, 100, 2000, 800), physical.Bounds) == new PixelRect(3000, 100, 840, 800));
            Check(PointerClipPolicy.Intersect(virtualScreen.Bounds, physical.Bounds) is null);
        });
        test("previous unrestricted desktop releases through NULL", () =>
        {
            var ownership = new PointerClipOwnership();
            ownership.Acquire(desktop, physical.Bounds, desktop, [physical, virtualScreen]);
            var restore = ownership.Restoration(physical.Bounds, [physical, virtualScreen]);
            Check(restore is not null && restore.Bounds is null);
        });
        test("previous custom clip is restored only with unchanged topology and owned current rectangle", () =>
        {
            var ownership = new PointerClipOwnership();
            var custom = new PixelRect(100, 100, 4500, 1500);
            var applied = PointerClipPolicy.Intersect(custom, physical.Bounds)!.Value;
            ownership.Acquire(custom, applied, desktop, [physical, virtualScreen]);
            Check(ownership.Restoration(applied, [physical with { }, virtualScreen with { }])?.Bounds == custom);
        });
        test("another application changing the clip relinquishes ownership without restoration", () =>
        {
            var ownership = new PointerClipOwnership();
            ownership.Acquire(desktop, physical.Bounds, desktop, [physical, virtualScreen]);
            Check(ownership.Restoration(new(0, 0, 2000, 1000), [physical, virtualScreen]) is null && ownership.Applied is null);
        });
        test("monitor hotplug releases owned clip without restoring stale custom coordinates", () =>
        {
            var ownership = new PointerClipOwnership();
            var custom = new PixelRect(100, 100, 4500, 1500);
            var applied = PointerClipPolicy.Intersect(custom, physical.Bounds)!.Value;
            ownership.Acquire(custom, applied, desktop, [physical, virtualScreen]);
            var restore = ownership.Restoration(applied, [physical]);
            Check(restore is not null && restore.Bounds is null);
        });
        test("a preexisting matching rectangle is not claimed as our clip", () =>
        {
            var ownership = new PointerClipOwnership();
            ownership.Acquire(physical.Bounds, physical.Bounds, desktop, [physical, virtualScreen]);
            Check(ownership.Applied is null && ownership.Restoration(physical.Bounds, [physical, virtualScreen]) is null);
        });
        test("empty snapshots perform no native reads or writes", () =>
        {
            var backend = new ClipBackend(desktop);
            var manager = new PointerClipManager(backend);
            manager.Update([]); manager.Release([]);
            Check(backend.Reads == 0 && backend.Writes.Count == 0 && !manager.State.Applied);
        });
        test("clip manager applies full physical pixels once and releases unrestricted desktop through NULL", () =>
        {
            var backend = new ClipBackend(desktop);
            var manager = new PointerClipManager(backend);
            manager.Update([physical, virtualScreen]); manager.Update([physical with { }, virtualScreen with { }]);
            Check(backend.Writes.SequenceEqual(new PixelRect?[] { physical.Bounds }) && manager.State.Applied &&
                manager.State.Bounds == physical.Bounds && manager.State.Current == physical.Bounds);
            manager.Release([physical, virtualScreen]);
            Check(backend.Writes.Last() is null && backend.Current == desktop && !manager.State.Applied);
        });
        test("clip manager never expands a third-party half-height clip", () =>
        {
            var backend = new ClipBackend(desktop) { Current = new(0, 0, 3840, 1080) };
            var manager = new PointerClipManager(backend);
            manager.Update([physical, virtualScreen]); manager.Release([physical, virtualScreen]);
            Check(backend.Writes.Count == 0 && !manager.State.Applied && manager.State.Current == backend.Current);
        });
        test("third-party narrow clip replaces ownership and survives subsequent release", () =>
        {
            var backend = new ClipBackend(desktop);
            var manager = new PointerClipManager(backend);
            manager.Update([physical, virtualScreen]);
            backend.Current = new(100, 100, 3000, 900);
            manager.Update([physical, virtualScreen]); manager.Release([physical, virtualScreen]);
            Check(backend.Writes.Count == 1 && !manager.State.Applied && backend.Current == new PixelRect(100, 100, 3000, 900));
        });
        test("custom clip intersection and restoration use fake native backend", () =>
        {
            var custom = new PixelRect(3000, 100, 2000, 800);
            var backend = new ClipBackend(desktop) { Current = custom };
            var manager = new PointerClipManager(backend);
            manager.Update([physical, virtualScreen]);
            Check(backend.Current == new PixelRect(3000, 100, 840, 800));
            manager.Release([physical, virtualScreen]);
            Check(backend.Writes.Last() == custom && backend.Current == custom && !manager.State.Applied);
        });
        test("failed release retains ownership so a lease cannot be granted prematurely", () =>
        {
            var backend = new ClipBackend(desktop);
            var manager = new PointerClipManager(backend);
            manager.Update([physical, virtualScreen]);
            backend.FailWrite = true; manager.Release([physical, virtualScreen]);
            Check(manager.State.Applied && manager.State.Error is not null && backend.Current == physical.Bounds);
            backend.FailWrite = false; manager.Release([physical, virtualScreen]);
            Check(!manager.State.Applied && manager.State.Error is null && backend.Current == desktop);
        });
        test("failed read retains owned clip and reports diagnostics", () =>
        {
            var backend = new ClipBackend(desktop);
            var manager = new PointerClipManager(backend);
            manager.Update([physical, virtualScreen]);
            backend.FailRead = true; manager.Release([physical, virtualScreen]);
            Check(manager.State.Applied && manager.State.Current is null && manager.State.Error is not null && backend.Writes.Count == 1);
        });
        test("hotplug release uses current desktop rather than a saved custom rectangle", () =>
        {
            var backend = new ClipBackend(desktop) { Current = new(3000, 100, 2000, 800) };
            var manager = new PointerClipManager(backend);
            manager.Update([physical, virtualScreen]);
            backend.Desktop = physical.Bounds; manager.Release([physical]);
            Check(backend.Writes.Last() is null && backend.Current == physical.Bounds && !manager.State.Applied);
        });
    }

    private sealed class ClipBackend(PixelRect desktop) : IPointerClipBackend
    {
        public PixelRect Desktop { get; set; } = desktop;
        public PixelRect Current { get; set; } = desktop;
        public bool FailRead { get; set; }
        public bool FailWrite { get; set; }
        public int Reads { get; private set; }
        public List<PixelRect?> Writes { get; } = [];
        public string? Error => "Fake native clip failure";
        public PixelRect? Read() { Reads++; return FailRead ? null : Current; }
        public bool Apply(PixelRect? rectangle)
        {
            Writes.Add(rectangle);
            if (FailWrite) return false;
            Current = rectangle ?? Desktop;
            return true;
        }
    }
}
