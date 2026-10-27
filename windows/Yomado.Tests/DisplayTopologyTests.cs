using Yomado.Core;

static class DisplayTopologyTests
{
    public static void Register(Action<string, Action> test)
    {
        DisplayInfo Physical() => new("physical", "source1", "Main", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), true, false);
        DisplayInfo Virtual() => new("virtual", "source2", "Codex", new(1920, 0, 1920, 1080), new(1920, 0, 1920, 1040), false, true, 0);
        void Reject(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Unsafe topology accepted"); }
        test("topology accepts one extended display without disturbing existing screens", () =>
            DisplayTopology.VerifyAddition([Physical()], [Physical(), Virtual()], new(0, "Codex")));
        test("mirrored physical source blocks screen creation", () =>
            Reject(() => DisplayTopology.RequireExtended([Physical() with { IsMirrored = true }])));
        test("overlapping desktop sources block migration", () =>
            Reject(() => DisplayTopology.RequireExtended([Physical(), Virtual() with { Bounds = Physical().Bounds }])));
        test("virtual primary blocks screen creation", () =>
            Reject(() => DisplayTopology.RequireExtended([Physical() with { IsPrimary = false }, Virtual() with { IsPrimary = true }])));
        test("a concurrent hotplug is not treated as our added display", () =>
            Reject(() => DisplayTopology.VerifyAddition([Physical()], [Physical(), Virtual(), Physical() with { Id = "new" }], new(0, "Codex"))));
        test("changed physical DPI blocks topology confirmation", () =>
            Reject(() => DisplayTopology.VerifyAddition([Physical()], [Physical() with { Scale = 1.5 }, Virtual()], new(0, "Codex"))));
        test("changed physical rotation blocks topology confirmation", () =>
            Reject(() => DisplayTopology.VerifyAddition([Physical()], [Physical() with { Rotation = 2 }, Virtual()], new(0, "Codex"))));
        test("unexpected virtual resolution blocks topology confirmation", () =>
            Reject(() => DisplayTopology.VerifyAddition([Physical()], [Physical(), Virtual() with { Bounds = new(1920, 0, 2560, 1440) }], new(0, "Codex"))));
        test("close preserves the sibling display mode and position", () =>
            DisplayTopology.VerifyRemoval([Physical(), Virtual()], [Physical()], "virtual"));
        test("close reports unexpected physical mode changes", () =>
            Reject(() => DisplayTopology.VerifyRemoval([Physical(), Virtual()], [Physical() with { OutputScaling = 2 }], "virtual")));
        test("profile names reject embedded control characters", () =>
        { if (new DisplayProfile(0, "Codex\nBrowser").IsValid) throw new Exception("Invalid profile accepted"); });
    }
}
