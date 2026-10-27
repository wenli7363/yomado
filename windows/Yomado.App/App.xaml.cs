using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Yomado.Windows;

namespace Yomado.App;

public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Palette.Apply();
        if (e.Args.Length is 1 or 2 && e.Args[0] == "--install-driver" && (e.Args.Length == 1 || e.Args[1] == "--trust-local-development"))
        { RunDriverInstaller(e.Args.Length == 2); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--test-driver-setup") { DriverSetupSmoke.Run(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--fixture-window") { NativeSmoke.Fixture(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--native-window-test") { NativeSmoke.Run(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--virtual-display-test") { NativeSmoke.VirtualRun(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--smoke-panel")
        {
            var panel = new MainWindow(readOnlyProbe: true) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
            panel.Show();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => { panel.WritePanelProbe(e.Args[1]); Shutdown(); }));
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--preview-ui")
        {
            // Explicit theme selection is used only for deterministic preview fixtures.
#pragma warning disable WPF0001
            ThemeMode = e.Args.Contains("dark") ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
            Palette.Apply(e.Args.Contains("dark"));
            var useDesktop = e.Args.Contains("--desktop");
            var preview = new MainWindow(preview: !useDesktop, readOnlyProbe: useDesktop) { Width = 1180, Height = 570, ShowInTaskbar = false, ShowActivated = false,
                WindowStyle = WindowStyle.None, Left = -20000, Top = -20000 };
            preview.Show();
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                preview.UpdateLayout();
                var scale = e.Args.Contains("--225-percent") ? 2.25 : 1;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(preview.ActualWidth * scale), (int)Math.Ceiling(preview.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(preview);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(e.Args[1]))!);
                using (var file = File.Create(e.Args[1])) encoder.Save(file);
                if (useDesktop) preview.DisposeProbe();
                Shutdown();
            }));
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--diagnose")
        {
            try
            {
                using var desktop = new Desktop(); using var driver = new DriverClient();
                var includeIds = e.Args.Contains("--include-device-identifiers");
                var displays = desktop.GetDisplays();
                var inventory = desktop.GetWindows();
                var health = DriverHealth.Inspect();
                var diagnostic = new
                {
                    version = "0.1.0", driverAvailable = driver.IsAvailable, driverCondition = health.Condition.ToString(), driverMessage = health.Message,
                    displays = displays.Select(d => new { id = includeIds ? d.Id :
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(d.Id)))[..12], d.Name, d.Bounds, d.WorkArea,
                        d.IsPrimary, d.IsYomado, d.Scale, windowCount = inventory.Count(w => Core.GeometryPolicy.Owner(w.Frame, displays)?.Id == d.Id) })
                };
                File.WriteAllText(e.Args[1], JsonSerializer.Serialize(diagnostic, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
            catch (Exception ex) { File.WriteAllText(e.Args[1], JsonSerializer.Serialize(new { error = ex.Message })); Shutdown(1); }
            return;
        }
        instance = new Mutex(true, "Local\\Yomado.Windows", out var first);
        if (!first) { PostMessage((nint)0xffff, global::Yomado.App.MainWindow.ActivateMessage, 0, 0); Shutdown(); return; }
        DispatcherUnhandledException += (_, error) =>
        {
            MessageBox.Show(error.Exception.Message, "yomado", MessageBoxButton.OK, MessageBoxImage.Warning);
            error.Handled = true;
        };
        try { MainWindow = new MainWindow(); MainWindow.Show(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "无法启动 yomado", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
    private async void RunDriverInstaller(bool approveDevelopmentTrust)
    { Shutdown((int)await DriverInstaller.RunElevatedAsync(AppContext.BaseDirectory, approveDevelopmentTrust)); }
    public static void OpenLink(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    [System.Runtime.InteropServices.DllImport("user32.dll")] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
