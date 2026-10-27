using Yomado.Core;

internal static class CursorBoundaryTests
{
    public static void Register(Action<string, Action> test)
    {
        var physical = new DisplayInfo("physical", "physical", "Main", new(0, 0, 3840, 2160), new(0, 0, 3840, 2100), true, false, Scale: 2.25);
        var virtualScreen = new DisplayInfo("virtual", "virtual", "Yomado", new(3840, 0, 1920, 1080), new(3840, 0, 1920, 1040), false, true);
        var left = physical with { Id = "left", Bounds = new(-1920, 0, 1920, 1080), IsPrimary = false };
        static void Check(PixelPoint actual, PixelPoint expected) { if (actual != expected) throw new Exception($"Expected {expected}, got {actual}."); }
        test("right edge stops physical motion before entering Yomado", () =>
            Check(CursorBoundaryPolicy.Constrain(new(3830, 500), new(3900, 520), [physical, virtualScreen]), new(3839, 520)));
        test("screenshot tool repositioning into shorter virtual screen stays on physical screen", () =>
        {
            Check(CursorBoundaryPolicy.Constrain(new(3839, 1000), new(3934, 1000), [physical, virtualScreen]), new(3839, 1000));
            Check(CursorBoundaryPolicy.Constrain(new(3839, 1050), new(3986, 1050), [physical, virtualScreen]), new(3839, 1050));
            Check(CursorBoundaryPolicy.Constrain(new(3839, 1050), new(3839, 2159), [physical, virtualScreen]), new(3839, 2159));
        });
        test("explicit input lease permits the same virtual-screen positioning", () =>
        {
            Check(CursorBoundaryPolicy.Constrain(new(3839, 1000), new(3934, 1000), [physical, virtualScreen], allowVirtualInput: true), new(3934, 1000));
            Check(CursorBoundaryPolicy.Constrain(new(3934, 1000), new(3986, 1050), [physical, virtualScreen], allowVirtualInput: true), new(3986, 1050));
        });
        test("ending lease allows virtual cursor to return then restores entry boundary", () =>
        {
            Check(CursorBoundaryPolicy.Constrain(new(3934, 1000), new(3830, 1000), [physical, virtualScreen], allowVirtualInput: false), new(3830, 1000));
            Check(CursorBoundaryPolicy.Constrain(new(3830, 1000), new(3986, 1000), [physical, virtualScreen], allowVirtualInput: false), new(3839, 1000));
        });
        test("pointer motion uses the full physical pixel height at mixed DPI", () =>
            Check(CursorBoundaryPolicy.Constrain(new(3800, 1000), new(3839, 2159), [physical, virtualScreen]), new(3839, 2159)));
        test("movement between existing physical displays remains available", () =>
            Check(CursorBoundaryPolicy.Constrain(new(10, 500), new(-20, 510), [physical, virtualScreen, left]), new(-20, 510)));
        test("other vendors virtual displays remain available", () =>
            Check(CursorBoundaryPolicy.Constrain(new(10, 500), new(-20, 510), [physical, virtualScreen, left with { IsExternalVirtual = true }]), new(-20, 510)));
        test("cursor already on a Yomado screen can move and return", () =>
        {
            Check(CursorBoundaryPolicy.Constrain(new(3900, 500), new(4100, 600), [physical, virtualScreen]), new(4100, 600));
            Check(CursorBoundaryPolicy.Constrain(new(3900, 500), new(3800, 600), [physical, virtualScreen]), new(3800, 600));
        });
        test("unknown cursor location after topology change fails open", () =>
            Check(CursorBoundaryPolicy.Constrain(new(-5000, -5000), new(3900, 500), [physical, virtualScreen]), new(3900, 500)));
        test("bottom edge uses monitor bounds rather than taskbar work area", () =>
        {
            var below = virtualScreen with { Bounds = new(0, 2160, 1920, 1080) };
            Check(CursorBoundaryPolicy.Constrain(new(100, 2150), new(110, 2200), [physical, below]), new(110, 2159));
        });
        test("negative coordinate screen boundary preserves its desktop origin", () =>
        {
            var outsideLeft = virtualScreen with { Bounds = new(-3840, 0, 1920, 1080) };
            Check(CursorBoundaryPolicy.Constrain(new(-1900, 100), new(-2000, 120), [physical, left, outsideLeft]), new(-1920, 120));
        });
        test("no managed display means no pointer restriction", () =>
            Check(CursorBoundaryPolicy.Constrain(new(3800, 100), new(3900, 120), [physical]), new(3900, 120)));
        test("position reconciliation blocks repeated crossings missed by the mouse hook", () =>
        {
            var tracker = new CursorBoundaryTracker();
            Check(tracker.Observe(new(3830, 1000), [physical, virtualScreen]), new(3830, 1000));
            Check(tracker.Observe(new(3934, 1000), [physical, virtualScreen]), new(3839, 1000));
            Check(tracker.Observe(new(3986, 1050), [physical, virtualScreen]), new(3839, 1050));
            Check(tracker.Observe(new(3839, 2159), [physical, virtualScreen]), new(3839, 2159));
        });
        test("position reconciliation leaves a cursor starting on virtual free until ordinary return", () =>
        {
            var tracker = new CursorBoundaryTracker();
            Check(tracker.Observe(new(3934, 1000), [physical, virtualScreen]), new(3934, 1000));
            Check(tracker.Observe(new(3986, 1050), [physical, virtualScreen]), new(3986, 1050));
            Check(tracker.Observe(new(3830, 1000), [physical, virtualScreen]), new(3830, 1000));
            Check(tracker.Observe(new(3934, 1000), [physical, virtualScreen]), new(3839, 1000));
        });
        test("granted lease can leave cursor virtual after expiry until ordinary return", () =>
        {
            var tracker = new CursorBoundaryTracker();
            tracker.Observe(new(3830, 1000), [physical, virtualScreen]);
            Check(tracker.Observe(new(3934, 1000), [physical, virtualScreen], allowVirtualInput: true), new(3934, 1000));
            Check(tracker.Observe(new(3986, 1050), [physical, virtualScreen]), new(3986, 1050));
            Check(tracker.Observe(new(3830, 1000), [physical, virtualScreen]), new(3830, 1000));
            Check(tracker.Observe(new(3934, 1000), [physical, virtualScreen]), new(3839, 1000));
        });
        test("lease suspension clears anchor even when no hook callback observes authorized movement", () =>
        {
            var tracker = new CursorBoundaryTracker();
            tracker.Observe(new(3830, 1000), [physical, virtualScreen]);
            tracker.Suspend();
            Check(tracker.Observe(new(3934, 1000), [physical, virtualScreen]), new(3934, 1000));
        });
        test("changed topology invalidates an ordinary cursor anchor", () =>
        {
            var tracker = new CursorBoundaryTracker();
            tracker.Observe(new(3830, 1000), [physical, virtualScreen]);
            var changed = physical with { Bounds = new(0, 0, 2560, 1440) };
            Check(tracker.Observe(new(3934, 1000), [changed, virtualScreen]), new(3934, 1000));
        });
        test("equivalent refreshed topology keeps protection across missed events", () =>
        {
            var tracker = new CursorBoundaryTracker();
            tracker.Observe(new(3830, 1000), [physical, virtualScreen]);
            Check(tracker.Observe(new(3934, 1000), [physical with { }, virtualScreen with { }]), new(3839, 1000));
        });
        test("position reconciliation permits motion to a disjoint ordinary screen", () =>
        {
            var tracker = new CursorBoundaryTracker();
            var farLeft = left with { Bounds = new(-6000, -3000, 1920, 1080) };
            tracker.Observe(new(3830, 1000), [physical, virtualScreen, farLeft]);
            Check(tracker.Observe(new(-5000, -2500), [physical, virtualScreen, farLeft]), new(-5000, -2500));
        });
    }
}
