using Yomado.Core;

internal static class ClosePolicyTests
{
    public static void Register(Action<string, Action> test)
    {
        var physical = new DisplayInfo("main", "main", "main", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), true, false);
        var one = new DisplayInfo("one", "one", "one", new(1920, 0, 1920, 1080), new(1920, 0, 1920, 1040), false, true, 0);
        var two = one with { Id = "two", Slot = 1, Bounds = new(3840, 0, 1920, 1080), WorkArea = new(3840, 0, 1920, 1040) };
        DisplayInfo[] displays = [physical, one, two];
        var dialog = new WindowInfo(11, 42, 1234, "editor.exe", "Editor", new(2200, 100, 400, 300), false)
            { Generation = 5, IsDialog = true };
        CloseSnapshot Capture(params WindowInfo[] windows) => ClosePolicy.Capture(one, displays, windows, []);
        void Check(bool value) { if (!value) throw new Exception("Close policy assertion failed"); }
        void Throws(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Expected InvalidOperationException");
        }
        test("close confirmation includes only the selected display", () =>
        {
            var snapshot = Capture(dialog, dialog with { Handle = 12, Frame = new(100, 100, 400, 300) },
                dialog with { Handle = 13, Frame = new(4000, 100, 400, 300) });
            Check(snapshot.Windows.Count == 1 && snapshot.Windows[0].Handle == dialog.Handle);
        });
        test("close prompt aggregates applications without window contents", () =>
        {
            var snapshot = Capture(dialog, dialog with { Handle = 12 });
            Check(snapshot.Applications.Count == 1 && snapshot.Applications[0] == new CloseApplication("Editor", 2));
        });
        test("unconfirmed dialogs cannot be left behind", () => Check(!ClosePolicy.CanRemove(Capture(dialog), Capture(dialog), false)));
        test("exact confirmed dialog can be left for Windows relocation", () => Check(ClosePolicy.CanRemove(Capture(dialog), Capture(dialog), true)));
        test("a new dialog invalidates close consent", () => Check(!ClosePolicy.CanRemove(Capture(dialog), Capture(dialog, dialog with { Handle = 12 }), true)));
        test("same process recycled window handle invalidates close consent", () => Check(!ClosePolicy.CanRemove(Capture(dialog), Capture(dialog with { Generation = 6 }), true)));
        test("reused PID invalidates close consent", () => Check(!ClosePolicy.CanRemove(Capture(dialog), Capture(dialog with { ProcessStart = 999 }), true)));
        test("dialog geometry changes invalidate close consent", () => Check(!ClosePolicy.CanRemove(Capture(dialog), Capture(dialog with { Frame = dialog.Frame with { X = 2250 } }), true)));
        test("a dialog that closed while prompting does not block closure", () => Check(ClosePolicy.CanRemove(Capture(dialog), Capture(), true)));
        test("consent for one display cannot authorize another", () => Check(!ClosePolicy.CanRemove(Capture(dialog), new(two, [dialog]), true)));
        test("display topology changes invalidate close consent", () => Check(!ClosePolicy.CanRemove(Capture(dialog), Capture(dialog) with { Display = one with { Scale = 1.25 } }, true)));
        test("a protected process with unknown identity cannot be waived", () => Throws(() => Capture(dialog with { ProcessStart = 0 })));
        test("unknown identity on another display does not block the selected display", () =>
            Check(Capture(dialog with { ProcessStart = 0, Frame = new(100, 100, 400, 300) }).Windows.Count == 0));
        test("a remembered origin cannot be waived as an unmovable dialog", () => Throws(() => ClosePolicy.Capture(one, displays, [dialog],
            [new(dialog, physical.Id, dialog.Frame, null, false, one.Id)])));
        test("workspace refuses closure while an ordinary window remains", () =>
        {
            var desktop = new CloseDesktop(displays, [dialog with { IsDialog = false }], []);
            Throws(() => new Workspace(desktop).InspectClose(one.Id));
        });
        test("workspace checks new blockers immediately before removal", () =>
        {
            var desktop = new CloseDesktop(displays, [], [dialog]);
            var workspace = new Workspace(desktop); var offered = workspace.InspectClose(one.Id);
            desktop.Blockers = [dialog, dialog with { Handle = 12 }];
            Throws(() => workspace.VerifyClose(offered, true));
        });
    }

    private sealed class CloseDesktop(IReadOnlyList<DisplayInfo> displays, IReadOnlyList<WindowInfo> windows,
        IReadOnlyList<WindowInfo> blockers) : IDesktop
    {
        public IReadOnlyList<WindowInfo> Blockers { get; set; } = blockers;
        public IReadOnlyList<DisplayInfo> GetDisplays() => displays;
        public IReadOnlyList<WindowInfo> GetWindows() => windows;
        public IReadOnlyList<WindowInfo> GetUnmovableWindows() => Blockers;
        public WindowInfo? GetWindow(nint handle) => windows.Concat(Blockers).FirstOrDefault(window => window.Handle == handle);
        public bool Place(WindowInfo identity, PixelRect frame, bool maximize) => false;
    }
}
