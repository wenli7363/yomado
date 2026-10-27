using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Yomado.Core;
using Yomado.Windows;

namespace Yomado.App;

internal static class NativeSmoke
{
    public static void Fixture(string handshake)
    {
        var window = new Window { Title = "Yomado native fixture", Width = 500, Height = 320, Left = 40, Top = 40,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        window.SourceInitialized += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x08000000); // WS_EX_NOACTIVATE: fixture only
            File.WriteAllText(handshake, JsonSerializer.Serialize(new { handle = (long)handle, process = Environment.ProcessId }));
        };
        window.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
        timer.Tick += (_, _) => Application.Current.Shutdown(); timer.Start();
    }

    public static async void Run(string report)
    {
        var output = Path.GetFullPath(report); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var handshake = Path.Combine(Path.GetDirectoryName(output)!, $"native-fixture-{Guid.NewGuid():N}.json");
        Process? child = null; var passed = false;
        try
        {
            var hostPath = Environment.ProcessPath ?? throw new InvalidOperationException("Runtime executable was not found.");
            var start = new ProcessStartInfo(hostPath) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(hostPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            start.ArgumentList.Add("--fixture-window"); start.ArgumentList.Add(handshake);
            var foreground = GetForegroundWindow(); child = Process.Start(start)!;
            for (var retry = 0; retry < 100 && !File.Exists(handshake); retry++) await Task.Delay(50);
            if (!File.Exists(handshake)) throw new TimeoutException("Native fixture did not start.");
            await Task.Delay(200);
            using var json = JsonDocument.Parse(File.ReadAllText(handshake));
            var handle = (nint)json.RootElement.GetProperty("handle").GetInt64();
            if (json.RootElement.GetProperty("process").GetInt32() != child.Id) throw new InvalidDataException("Wrong fixture process.");
            using var desktop = new Desktop(); var original = desktop.GetWindow(handle) ?? throw new InvalidOperationException("Fixture not found.");
            if (original.ProcessId != child.Id) throw new InvalidDataException("Wrong native window identity.");
            var physical = GeometryPolicy.Physical(desktop.GetDisplays()) ?? throw new InvalidOperationException("No physical screen.");
            var destination = GeometryPolicy.Place(new(0, 0, 650, 440), physical, new(180, 160, 650, 440));
            if (!desktop.Place(original, destination, false)) throw new InvalidOperationException("Native placement was not verified.");
            var moved = desktop.GetWindow(handle)!;
            if (!moved.Frame.Near(destination)) throw new InvalidOperationException("Native geometry differs from requested bounds.");
            if (!desktop.Place(moved, original.Frame, false)) throw new InvalidOperationException("Native restoration failed.");
            var restored = desktop.GetWindow(handle)!;
            passed = restored.Frame.Near(original.Frame);
            ShowWindow(handle, 7); // SW_SHOWMINNOACTIVE, only the invisible fixture
            await Task.Delay(100);
            var minimized = desktop.GetWindow(handle)!;
            if (!minimized.Minimized || !desktop.Place(minimized, destination, false)) throw new InvalidOperationException("Minimized fixture placement failed.");
            var minimizedMove = desktop.GetWindow(handle)!;
            if (!minimizedMove.Minimized || !desktop.Place(minimizedMove, original.Frame, false)) throw new InvalidOperationException("Minimized restoration failed.");
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed, nativeMove = moved.Frame, nativeRestore = restored.Frame,
                minimizedMove = minimizedMove.Frame, minimizedPreserved = minimizedMove.Minimized,
                focusUnchanged = foreground == GetForegroundWindow(), scope = "One invisible fixture window in a separate child process; no virtual monitors or user windows changed." },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { passed = false; File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, error = ex.Message })); }
        finally
        {
            if (child is { HasExited: false }) { child.Kill(); await child.WaitForExitAsync(); }
            child?.Dispose(); if (File.Exists(handshake)) File.Delete(handshake);
            Application.Current.Shutdown(passed ? 0 : 1);
        }
    }

    // Explicitly invoked developer acceptance test. Inventory is restricted to
    // our child HWND so shared runtime paths cannot select a user's windows.
    public static async void VirtualRun(string report)
    {
        var output = Path.GetFullPath(report); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var handshake = Path.Combine(Path.GetDirectoryName(output)!, $"virtual-fixture-{Guid.NewGuid():N}.json");
        var steps = new List<object>(); var failures = new List<string>();
        using var singleInstance = new Mutex(true, "Local\\Yomado.Windows", out var first);
        using var desktop = new Desktop(); using var driver = new DriverClient();
        var baseline = desktop.GetDisplays(); var created = new List<uint>();
        var foreground = GetForegroundWindow(); Process? child = null; WindowInfo? original = null;
        Workspace? workspace = null; var passed = false;
        try
        {
            if (!first || baseline.Any(d => d.IsYomado)) throw new InvalidOperationException("Exit Yomado and close its screens before this explicit acceptance test.");
            if (GeometryPolicy.Physical(baseline) is null) throw new InvalidOperationException("A physical display is required.");
            await driver.EnsureConnectedAsync();
            if (driver.GetConnectedSlots().Count != 0) throw new InvalidOperationException("Driver already has connected screens.");
            async Task<DisplayInfo> Create(DisplayProfile profile)
            {
                var before = desktop.GetDisplays(); await driver.CreateAsync(profile); created.Add(profile.Slot);
                DisplayInfo? display = null;
                for (var retry = 0; retry < 100; retry++)
                {
                    display = desktop.GetDisplays().SingleOrDefault(d => d.IsYomado && d.Slot == profile.Slot);
                    if (display is not null) break;
                    await Task.Delay(100);
                }
                if (display is null) throw new TimeoutException("Created monitor did not become active.");
                DisplayLayout.Append(display, before, desktop);
                display = desktop.GetDisplays().Single(d => d.IsYomado && d.Slot == profile.Slot);
                if (display.Bounds.Width != profile.PixelWidth || display.Bounds.Height != profile.PixelHeight)
                    throw new InvalidOperationException("Monitor dimensions differ from its profile.");
                steps.Add(new { action = "create", profile.Slot, display.Bounds, display.Scale, display.IsPrimary });
                return display;
            }
            async Task Remove(uint slot)
            {
                driver.Remove(slot);
                for (var retry = 0; retry < 100; retry++)
                {
                    if (!desktop.GetDisplays().Any(d => d.IsYomado && d.Slot == slot) && !driver.GetConnectedSlots().Contains(slot))
                    { created.Remove(slot); steps.Add(new { action = "remove", slot }); return; }
                    await Task.Delay(100);
                }
                throw new TimeoutException("Monitor removal was not confirmed.");
            }
            var a = await Create(new(14, "Acceptance A"));
            var b = await Create(new(15, "Acceptance B", 2560, 1440));
            var hostPath = Environment.ProcessPath ?? throw new InvalidOperationException("Runtime executable was not found.");
            var start = new ProcessStartInfo(hostPath) { UseShellExecute = false, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(hostPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            start.ArgumentList.Add("--fixture-window"); start.ArgumentList.Add(handshake);
            child = Process.Start(start)!;
            for (var retry = 0; retry < 100 && !File.Exists(handshake); retry++) await Task.Delay(50);
            if (!File.Exists(handshake)) throw new TimeoutException("Fixture did not start.");
            await Task.Delay(200);
            using var json = JsonDocument.Parse(File.ReadAllText(handshake));
            var handle = (nint)json.RootElement.GetProperty("handle").GetInt64();
            original = desktop.GetWindow(handle) ?? throw new InvalidOperationException("Fixture window was not found.");
            if (original.ProcessId != child.Id || json.RootElement.GetProperty("process").GetInt32() != child.Id)
                throw new InvalidDataException("Fixture identity mismatch.");
            var source = GeometryPolicy.Owner(original.Frame, baseline) ?? throw new InvalidOperationException("Fixture has no physical origin.");
            workspace = new Workspace(new FixtureDesktop(desktop, original, steps.Add));
            workspace.MoveApplication(original.ApplicationPath, source.Id, a.Id);
            steps.Add(new { action = "move-to-A", frame = desktop.GetWindow(handle)!.Frame });
            workspace.MoveApplication(original.ApplicationPath, a.Id, b.Id);
            steps.Add(new { action = "move-A-to-B", frame = desktop.GetWindow(handle)!.Frame });
            workspace.RestoreDisplay(b.Id);
            if (!desktop.GetWindow(handle)!.Frame.Near(original.Frame) || workspace.Origins.Count != 0)
                throw new InvalidOperationException("Cross-screen original position was not restored.");
            steps.Add(new { action = "restore", frame = desktop.GetWindow(handle)!.Frame });
            workspace.MoveApplication(original.ApplicationPath, source.Id, a.Id);
            ShowWindow(handle, 7); await Task.Delay(100);
            workspace.RestoreDisplay(a.Id);
            var minimized = desktop.GetWindow(handle)!;
            if (!minimized.Minimized || !minimized.Frame.Near(original.Frame) || workspace.Origins.Count != 0)
                throw new InvalidOperationException("Minimized window did not return with its original state and geometry.");
            steps.Add(new { action = "restore-minimized", minimized.Frame, minimized.Minimized });
            await Remove(b.Slot!.Value);
            await Create(new(15, "Acceptance HiDPI 1080", 1920, 1080, true)); await Remove(15);
            await Create(new(15, "Acceptance HiDPI 1440", 2560, 1440, true)); await Remove(15);
            await Remove(a.Slot!.Value);
            passed = true;
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally
        {
            if (child is { HasExited: false })
            {
                if (original is not null && desktop.GetWindow(original.Handle) is { } live)
                {
                    if (!desktop.Place(live, original.NormalFrame, original.Maximized)) failures.Add("Final fixture restoration could not be verified.");
                }
                child.Kill(); await child.WaitForExitAsync();
            }
            child?.Dispose();
            foreach (var slot in created.ToArray())
            {
                try { driver.Remove(slot); } catch (Exception ex) { failures.Add($"Cleanup slot {slot}: {ex.Message}"); }
            }
            driver.Dispose();
            for (var retry = 0; retry < 60 && desktop.GetDisplays().Count != baseline.Count; retry++) await Task.Delay(100);
            var after = desktop.GetDisplays();
            var physicalUnchanged = baseline.All(old => after.Any(d => d.Id == old.Id && d.Bounds.Near(old.Bounds, 0) && d.IsPrimary == old.IsPrimary && d.Scale == old.Scale));
            var screensRemoved = after.Count == baseline.Count;
            for (var retry = 0; retry < 20 && foreground != GetForegroundWindow(); retry++) await Task.Delay(50);
            passed = passed && failures.Count == 0 && physicalUnchanged && screensRemoved && foreground == GetForegroundWindow();
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed, steps, failures, physicalUnchanged, screensRemoved,
                focusUnchanged = foreground == GetForegroundWindow(), foregroundBefore = (long)foreground, foregroundAfter = (long)GetForegroundWindow(),
                scope = "Explicit monitor acceptance using one invisible child fixture only; no user-window moves, input, or profile writes." }, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(handshake)) File.Delete(handshake);
            Application.Current.Shutdown(passed ? 0 : 1);
        }
    }

    private sealed class FixtureDesktop(Desktop desktop, WindowInfo fixture, Action<object> trace) : IDesktop
    {
        public IReadOnlyList<DisplayInfo> GetDisplays() => desktop.GetDisplays();
        public IReadOnlyList<WindowInfo> GetWindows() => desktop.GetWindow(fixture.Handle) is { Minimized: false } live && live.ProcessId == fixture.ProcessId ? [live] : [];
        public WindowInfo? GetWindow(nint handle) => handle == fixture.Handle ? desktop.GetWindow(handle) : null;
        public bool Place(WindowInfo window, PixelRect frame, bool maximize)
        {
            if (window.Handle != fixture.Handle || window.ProcessId != fixture.ProcessId) return false;
            var beforeDpi = GetDpiForWindow(window.Handle);
            var result = desktop.Place(window, frame, maximize);
            trace(new { action = "native-place", requested = frame, actual = desktop.GetWindow(window.Handle)?.Frame, beforeDpi, afterDpi = GetDpiForWindow(window.Handle), verified = result });
            return result;
        }
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
}
