using System.Text.Json;
using Yomado.Core;

var tests = new List<(string Name, Action Run)>();
void Test(string name, Action run) => tests.Add((name, run));
void Check(bool condition, string message = "assertion failed") { if (!condition) throw new Exception(message); }
void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
DisplayInfo Display(string id, int x, bool virtualScreen = false, bool primary = false) => new(id, id, id, new(x, 0, 1920, 1080), new(x, 0, 1920, 1040), primary, virtualScreen);
var main = Display("main", 0, primary: true); var second = Display("second", -1920); var one = Display("one", 1920, true); var two = Display("two", 3840, true);
WindowInfo Window(int handle, int x, string app = "qdoc.exe") => new(handle, 50, 123, app, "QDoc", new(x, 100, 800, 600), false);
FakeDesktop Setup(params WindowInfo[] windows) => new([main, second, one, two], windows);

Test("greatest intersection assigns each window once", () => Check(GeometryPolicy.Owner(new(1800, 0, 800, 600), [main, one]) == one));
Test("equal intersection favors physical display", () => Check(GeometryPolicy.Owner(new(1820, 0, 200, 600), [one, main]) == main));
Test("fully offscreen geometry has no owner", () => Check(GeometryPolicy.Owner(new(8000, 0, 800, 600), [main, one]) is null));
Test("right edge includes other agent screens", () => Check(GeometryPolicy.RightEdge([second, main, one, two]) == 5760));
Test("negative desktop edges remain negative", () => Check(GeometryPolicy.RightEdge([Display("left", -4000)]) == -2080));
Test("remembered placement clamps inside current work area", () => Check(GeometryPolicy.Place(new(0, 0, 4000, 3000), main, new(1700, 900, 4000, 3000)) == main.WorkArea));
Test("external virtual screen is never physical fallback", () => Check(GeometryPolicy.Physical([main with { IsExternalVirtual = true }]) is null));
Test("preferred physical identity wins over main", () => Check(GeometryPolicy.Physical([main, second], "second") == second));
Test("source card only moves that app's windows on that screen", () =>
{
    var d = Setup(Window(1, 100), Window(2, -1700), Window(3, 300, "browser.exe")); var manager = new Workspace(d);
    manager.MoveApplication("qdoc.exe", "main", "one");
    Check(GeometryPolicy.Owner(d.Windows[1].Frame, d.Displays) == one && d.Windows[2].Frame.X == -1700 && d.Windows[3].Frame.X == 300);
});
Test("explicit whole app drop moves all visible app windows", () =>
{
    var d = Setup(Window(1, 100), Window(2, -1700)); var manager = new Workspace(d); manager.MoveApplication("qdoc.exe", null, "one"); Check(manager.Origins.Count == 2);
});
Test("moving between virtual screens retains first physical origin", () =>
{
    var d = Setup(Window(1, 100)); var manager = new Workspace(d); manager.MoveApplication("qdoc.exe", "main", "one");
    manager.MoveApplication("qdoc.exe", "one", "two"); manager.RestoreApplication("qdoc.exe", "two"); Check(d.Windows[1].Frame.X == 100 && manager.Origins.Count == 0);
});
Test("explicit return to another physical screen centers there", () =>
{
    var d = Setup(Window(1, 100)); var manager = new Workspace(d); manager.MoveApplication("qdoc.exe", "main", "one"); manager.RestoreApplication("qdoc.exe", "one", "second");
    Check(d.Windows[1].Frame.X == -1360 && GeometryPolicy.Owner(d.Windows[1].Frame, d.Displays) == second);
});
Test("missing original display falls back to remaining main", () =>
{
    var d = Setup(Window(1, -1700)); var manager = new Workspace(d); manager.MoveApplication("qdoc.exe", "second", "one"); d.Displays.Remove(second);
    manager.RestoreApplication("qdoc.exe", "one"); Check(GeometryPolicy.Owner(d.Windows[1].Frame, d.Displays) == main);
});
Test("manually moved untracked app restores to physical main", () =>
{
    var d = Setup(Window(1, 2200)); var manager = new Workspace(d); manager.RestoreDisplay("one"); Check(GeometryPolicy.Owner(d.Windows[1].Frame, d.Displays) == main);
});
Test("restore failure keeps checkpoint and prevents closure", () =>
{
    var d = Setup(Window(1, 100)); var manager = new Workspace(d); manager.MoveApplication("qdoc.exe", "main", "one"); d.FailMoves = true;
    Throws<InvalidOperationException>(() => manager.RestoreDisplay("one")); Check(manager.Origins.Count == 1 && GeometryPolicy.Owner(d.Windows[1].Frame, d.Displays) == one);
});
Test("failed initial move rolls back without fake success", () =>
{
    var d = Setup(Window(1, 100)); var manager = new Workspace(d); d.FailMoves = true;
    Throws<InvalidOperationException>(() => manager.MoveApplication("qdoc.exe", "main", "one")); Check(d.Windows[1].Frame.X == 100);
});
Test("PID reuse drops a stale origin", () =>
{
    var d = Setup(Window(1, 100)); var manager = new Workspace(d); manager.MoveApplication("qdoc.exe", "main", "one");
    d.Windows[1] = d.Windows[1] with { ProcessStart = 999 }; manager.RefreshOrigins(); Check(manager.Origins.Count == 0);
});
Test("same-process HWND reuse drops a stale origin", () =>
{
    var d = Setup(Window(1, 100)); var manager = new Workspace(d); manager.MoveApplication("qdoc.exe", "main", "one");
    d.Windows[1] = d.Windows[1] with { Generation = 9 }; manager.RefreshOrigins(); Check(manager.Origins.Count == 0);
});
Test("a closed managed window cannot block closing empty screen", () =>
{
    var d = Setup(Window(1, 100)); var manager = new Workspace(d); manager.MoveApplication("qdoc.exe", "main", "one"); d.Windows.Clear(); manager.RestoreDisplay("one"); Check(manager.Origins.Count == 0);
});
Test("manual physical return retires an origin", () =>
{
    var d = Setup(Window(1, 100)); var manager = new Workspace(d); manager.MoveApplication("qdoc.exe", "main", "one"); d.Windows[1] = Window(1, 200); manager.RefreshOrigins(); Check(manager.Origins.Count == 0);
});
Test("absence of physical screen preserves virtual window", () =>
{
    var d = Setup(Window(1, 2200)); d.Displays.Remove(main); d.Displays.Remove(second); var manager = new Workspace(d);
    Throws<InvalidOperationException>(() => manager.RestoreDisplay("one")); Check(d.Windows[1].Frame.X == 2200);
});
Test("missing requested physical target never silently chooses another", () =>
{
    var d = Setup(Window(1, 2200)); var manager = new Workspace(d); Throws<InvalidOperationException>(() => manager.RestoreApplication("qdoc.exe", "one", "missing")); Check(d.Windows[1].Frame.X == 2200);
});
Test("normal geometry of a maximized window is remembered", () =>
{
    var d = Setup(Window(1, 0) with { Maximized = true, NormalFrame = new(100, 120, 700, 500) }); var manager = new Workspace(d);
    manager.MoveApplication("qdoc.exe", "main", "one"); Check(manager.Origins.Single().OriginalFrame == new PixelRect(100, 120, 700, 500));
});
Test("HiDPI uses double pixels", () => Check(new DisplayProfile(0, "Codex", 2560, 1440, true).PixelWidth == 5120));
Test("invalid saved profile values cannot be written", () =>
{
    var file = Path.Combine(Path.GetTempPath(), $"yomado-test-{Guid.NewGuid()}.json"); var store = new ProfileStore(file);
    Throws<ArgumentException>(() => store.Save([new(0, "", 1920, 1080)])); Check(!File.Exists(file));
});
Test("duplicate profile IDs cannot be written", () =>
{
    var file = Path.Combine(Path.GetTempPath(), $"yomado-test-{Guid.NewGuid()}.json"); Throws<ArgumentException>(() => new ProfileStore(file).Save([new(0, "a"), new(0, "b")]));
});
Test("corrupt profile file is preserved", () =>
{
    var file = Path.Combine(Path.GetTempPath(), $"yomado-test-{Guid.NewGuid()}.json"); File.WriteAllText(file, "corrupt");
    try { Throws<InvalidDataException>(() => new ProfileStore(file).Load()); Check(File.ReadAllText(file) == "corrupt"); } finally { File.Delete(file); }
});
Test("prerelease ordering compares numerical identifiers numerically", () => Check(new SemanticVersion("1.0.0-rc.10").CompareTo(new("1.0.0-rc.2")) > 0));
Test("release outranks prerelease", () => Check(new SemanticVersion("1.0.0").CompareTo(new("1.0.0-rc.1")) > 0));
Test("build metadata does not change version ordering", () => Check(new SemanticVersion("v1.0.0+abc").CompareTo(new("1.0.0+def")) == 0));
Test("leading zero prerelease numbers are rejected", () => Throws<FormatException>(() => new SemanticVersion("1.0.0-01")));
Test("unbounded numerical version components do not overflow", () => Check(new SemanticVersion("999999999999999999999.0.0").CompareTo(new("2.0.0")) > 0));
Test("macOS releases never become Windows updates", () =>
{
    using var json = JsonDocument.Parse("[{\"draft\":false,\"tag_name\":\"v9.0.0\",\"assets\":[{\"name\":\"Yomado.app.zip\"}]}]");
    Throws<InvalidOperationException>(() => UpdateChecker.Evaluate(json.RootElement.EnumerateArray(), "0.1.0"));
});
Test("Windows update is selected by asset and version", () =>
{
    using var json = JsonDocument.Parse("[{\"draft\":false,\"tag_name\":\"v0.2.0\",\"assets\":[{\"name\":\"Yomado-Windows-0.2.0-x64.zip\"}],\"html_url\":\"https://invalid.example\"}]");
    var result = UpdateChecker.Evaluate(json.RootElement.EnumerateArray(), "0.1.0"); Check(result.IsNewer && result.ReleaseUrl == "https://github.com/wenli7363/yomado/releases/tag/v0.2.0");
});

var failures = 0;
Test("moving across DPI preserves logical window size", () =>
{
    var d = Setup(Window(1, 100)); d.Displays[0] = main with { Scale = 2.25 }; var manager = new Workspace(d);
    manager.MoveApplication("qdoc.exe", "main", "one"); Check(d.Windows[1].Frame.Width == 356 && d.Windows[1].Frame.Height == 267);
    manager.RestoreApplication("qdoc.exe", "one"); Check(d.Windows[1].Frame == Window(1, 100).Frame);
});
Test("return respects changed DPI on original monitor", () =>
{
    var d = Setup(Window(1, 100)); var manager = new Workspace(d); manager.MoveApplication("qdoc.exe", "main", "one");
    d.Displays[0] = main with { Scale = 1.5 }; manager.RestoreApplication("qdoc.exe", "one"); Check(d.Windows[1].Frame.Width == 1200 && d.Windows[1].Frame.X == 150);
});
Test("failed persistent checkpoint prevents window mutation", () =>
{
    var d = Setup(Window(1, 100)); var manager = new Workspace(d) { CheckpointBeforeMove = _ => throw new IOException("disk full") };
    Throws<IOException>(() => manager.MoveApplication("qdoc.exe", "main", "one")); Check(d.Windows[1].Frame.X == 100 && manager.Origins.Count == 0);
});
Test("checkpoint is recorded before any window movement", () =>
{
    var d = Setup(Window(1, 100)); var manager = new Workspace(d); var captured = false;
    manager.CheckpointBeforeMove = origin => { captured = d.Windows[1].Frame.X == 100 && origin.PhysicalDisplayId == "main" && origin.LastDisplayId == "one"; };
    manager.MoveApplication("qdoc.exe", "main", "one"); Check(captured);
});
Test("invalid scale cannot produce placement geometry", () => Throws<ArgumentException>(() => GeometryPolicy.Rescale(new(0,0,800,600), 0, 1)));
Test("invalid WeChat checkpoint token is rejected", () => Check(!new WeChatCheckpoint(1,2,3,"WeChat.exe",0,"main",new(0,0,800,600),null,false,"one").IsValid));
Test("a refused maximized state is not reported as restored", () => Check(!GeometryPolicy.PlacementVerified(Window(1,100), Window(1,100).Frame, main, true)));
Test("minimized windows must retain verified normal geometry", () => Check(!GeometryPolicy.PlacementVerified(Window(1,100) with { Minimized = true }, new(200,100,800,600), main, true)));
Test("null profile entries are safely ignored", () =>
{
    var file = Path.Combine(Path.GetTempPath(), $"yomado-test-{Guid.NewGuid()}.json"); File.WriteAllText(file,"[null]");
    try { Check(new ProfileStore(file).Load().Count == 0); } finally { File.Delete(file); }
});
DisplayTopologyTests.Register(Test);
ClosePolicyTests.Register(Test);
InputLeaseTests.Register(Test);
CursorBoundaryTests.Register(Test);
PointerClipTests.Register(Test);
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed");
return failures == 0 ? 0 : 1;

sealed class FakeDesktop(List<DisplayInfo> displays, IEnumerable<WindowInfo> windows) : IDesktop
{
    public List<DisplayInfo> Displays { get; } = displays;
    public Dictionary<nint, WindowInfo> Windows { get; } = windows.ToDictionary(window => window.Handle);
    public bool FailMoves { get; set; }
    public IReadOnlyList<DisplayInfo> GetDisplays() => Displays;
    public IReadOnlyList<WindowInfo> GetWindows() => Windows.Values.ToArray();
    public WindowInfo? GetWindow(nint handle) => Windows.GetValueOrDefault(handle);
    public bool Place(WindowInfo identity, PixelRect frame, bool maximize)
    {
        if (FailMoves) return false;
        Windows[identity.Handle] = identity with { Frame = frame, Maximized = maximize };
        return true;
    }
}
