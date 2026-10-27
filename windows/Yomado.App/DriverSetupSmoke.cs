using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using Yomado.Windows;

namespace Yomado.App;

internal static class DriverSetupSmoke
{
    public static async void Run(string report)
    {
        var cases = new List<string>(); var failures = new List<string>();
        var output = Path.GetFullPath(report); var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "setup-probe-" + Guid.NewGuid().ToString("N"));
        var root = AppContext.BaseDirectory;
        void Check(string name, Action action)
        { try { action(); cases.Add(name); } catch (Exception ex) { failures.Add(name + ": " + ex.Message); } }
        static void Reject(Action action)
        { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Invalid package was accepted."); }
        try
        {
            var before = DriverHealth.Inspect();
            Check("Native driver readiness is confirmed by its versioned protocol", () => { if (!before.IsReady) throw new InvalidOperationException(before.Message); });
            foreach (var member in new[] { "Yomado.exe", "driver/YomadoDriver.dll", "driver/YomadoDriver.cat", "driver/YomadoDevice.exe" })
                Check("Trusted signature: " + member, () => { if (Authenticode.Verify(Path.Combine(root, member)) != 0) throw new InvalidDataException("Signature rejected."); });
            Check("Complete signed package passes preflight", () => DriverInstaller.ValidatePackage(root, false));
            Directory.CreateDirectory(temporary); Directory.CreateDirectory(Path.Combine(temporary, "driver"));
            Check("Incomplete package is rejected", () => Reject(() => DriverInstaller.ValidatePackage(temporary, false)));
            foreach (var member in new[] { "Yomado.exe", "Yomado-Development.cer", "driver/YomadoDriver.inf", "driver/YomadoDriver.cat", "driver/YomadoDriver.dll", "driver/YomadoDevice.exe" })
                File.Copy(Path.Combine(root, member), Path.Combine(temporary, member));
            var tampered = Path.Combine(temporary, "Yomado.exe");
            using (var file = new FileStream(tampered, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            { file.Position = 0x400; var value = file.ReadByte(); file.Position = 0x400; file.WriteByte((byte)(value ^ 1)); }
            Check("Modified signed executable is rejected before elevation", () => Reject(() => DriverInstaller.ValidatePackage(temporary, false)));
            File.Copy(Path.Combine(root, "Yomado.exe"), tampered, true);
            File.WriteAllText(Path.Combine(temporary, "Yomado-Development.cer"), "wrong development identity");
            Check("Replacement development certificate is rejected", () => Reject(() => DriverInstaller.ValidatePackage(temporary, true)));
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                var result = await DriverInstaller.RunElevatedAsync(root, false);
                Check("Unelevated installer refuses system changes", () => { if (result != DriverInstallResult.AdminRequired) throw new InvalidOperationException(result.ToString()); });
            }
            Check("Read-only checks preserve active virtual displays", () => { if (DriverHealth.Inspect().ActiveScreens != before.ActiveScreens) throw new InvalidOperationException("Active display count changed."); });
        }
        catch (Exception ex) { failures.Add(ex.Message); }
        finally
        {
            if (Directory.Exists(temporary))
            {
                foreach (var file in Directory.EnumerateFiles(Path.Combine(temporary, "driver"))) File.Delete(file);
                Directory.Delete(Path.Combine(temporary, "driver"));
                foreach (var file in Directory.EnumerateFiles(temporary)) File.Delete(file);
                Directory.Delete(temporary);
            }
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = failures.Count == 0, cases, failures,
                scope = "Read-only live driver and signature checks; disposable corrupted package copies; no device or certificate changes." }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(failures.Count == 0 ? 0 : 1);
        }
    }
}
