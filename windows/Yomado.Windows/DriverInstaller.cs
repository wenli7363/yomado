using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace Yomado.Windows;

public enum DriverInstallResult { Success = 0, AdminRequired = 10, InvalidPackage = 11, ScreensInUse = 12, SignatureRejected = 13,
    StageFailed = 20, RegistrationFailed = 21, BindingFailed = 22, NotReady = 23, Cancelled = 24, Unexpected = 25, RestartRequired = 3010 }

public static class DriverInstaller
{
    // This exception is only for this local development signer. A release
    // signed by a normally trusted publisher never adds a certificate store entry.
    public const string DevelopmentThumbprint = "E4F4A2A622EEA4B311759C881C92855DF365ED0B";
    private const string DevelopmentCertificateHash = "A250F10750E5CF0BF43D8060DA81FA01103C49614450EB76E857C5A85C16782B";
    private static readonly string[] Members = ["Yomado.exe", "driver/YomadoDriver.inf", "driver/YomadoDriver.cat", "driver/YomadoDriver.dll", "driver/YomadoDevice.exe"];
    public static bool HasPayload(string root) => Members.All(member => File.Exists(Path.Combine(root, member)));

    public static bool NeedsDevelopmentTrust(string root)
    {
        if (!File.Exists(Path.Combine(root, "Yomado-Development.cer"))) return false;
        return !ContainsSigner(StoreName.Root) || !ContainsSigner(StoreName.TrustedPublisher);
    }

    private static bool ContainsSigner(StoreName name)
    {
        using var store = new X509Store(name, StoreLocation.LocalMachine); store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, DevelopmentThumbprint, false);
        try { return matches.Count != 0; }
        finally { foreach (var certificate in matches) certificate.Dispose(); }
    }

    public static void ValidatePackage(string root, bool allowDevelopmentSigner)
    {
        if (!HasPayload(root)) throw new InvalidDataException("驱动安装文件不完整，请重新解压完整 Windows 发布包。");
        var allowUntrusted = false;
        if (allowDevelopmentSigner)
        {
            using var certificate = LoadDevelopmentCertificate(root);
            allowUntrusted = true;
        }
        foreach (var member in Members.Where(member => !member.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)))
        {
            var path = Path.Combine(root, member); var status = Authenticode.Verify(path);
            if (status == 0) continue;
            // Only an untrusted chain for the pinned developer is eligible for
            // explicit trust. Bad digests and unsigned/replaced files fail here.
            if (!allowUntrusted || (status != unchecked((int)0x800B0109) && status != unchecked((int)0x800B010A)))
                throw new InvalidDataException($"安装文件签名验证失败（0x{status:X8}），请重新获取完整发布包。");
            // X509CertificateLoader accepts certificates, not Authenticode
            // containers. Use the legacy signed-file reader only to obtain DER.
#pragma warning disable SYSLIB0057
            using var signed = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var signer = X509CertificateLoader.LoadCertificate(signed.GetRawCertData());
            if (!signer.Thumbprint.Equals(DevelopmentThumbprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装文件的签名与本地开发证书不一致。");
        }
    }

    private static X509Certificate2 LoadDevelopmentCertificate(string root)
    {
        var bytes = File.ReadAllBytes(Path.Combine(root, "Yomado-Development.cer"));
        if (Convert.ToHexString(SHA256.HashData(bytes)) != DevelopmentCertificateHash)
            throw new InvalidDataException("随包开发证书不匹配。");
        var certificate = X509CertificateLoader.LoadCertificate(bytes);
        if (certificate.Thumbprint != DevelopmentThumbprint || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow)
        { certificate.Dispose(); throw new InvalidDataException("随包开发证书无效或已过期。"); }
        return certificate;
    }

    public static async Task<DriverInstallResult> LaunchAsync(string root, bool approveDevelopmentTrust)
    {
        ValidatePackage(root, approveDevelopmentTrust);
        var start = new ProcessStartInfo(Path.Combine(root, "Yomado.exe")) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--install-driver");
        if (approveDevelopmentTrust) start.ArgumentList.Add("--trust-local-development");
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动驱动安装程序。");
            await process.WaitForExitAsync();
            return Enum.IsDefined(typeof(DriverInstallResult), process.ExitCode) ? (DriverInstallResult)process.ExitCode : DriverInstallResult.Unexpected;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return DriverInstallResult.Cancelled; }
    }

    public static async Task<DriverInstallResult> RunElevatedAsync(string root, bool approveDevelopmentTrust)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return DriverInstallResult.AdminRequired;
        var locks = new List<FileStream>();
        try
        {
            if (!HasPayload(root)) return DriverInstallResult.InvalidPackage;
            // Keep the verified files read-only until staging and execution end.
            foreach (var member in Members) locks.Add(new FileStream(Path.Combine(root, member), FileMode.Open, FileAccess.Read, FileShare.Read));
            ValidatePackage(root, approveDevelopmentTrust);
            var health = DriverHealth.Inspect();
            if (health.ActiveScreens != 0 || health.Condition == DriverCondition.Duplicate) return DriverInstallResult.ScreensInUse;
            using (var desktop = new Desktop()) if (desktop.GetDisplays().Any(display => display.IsYomado)) return DriverInstallResult.ScreensInUse;
            if (NeedsDevelopmentTrust(root))
            {
                if (!approveDevelopmentTrust) return DriverInstallResult.SignatureRejected;
                using var certificate = LoadDevelopmentCertificate(root);
                foreach (var name in new[] { StoreName.Root, StoreName.TrustedPublisher })
                {
                    using var store = new X509Store(name, StoreLocation.LocalMachine); store.Open(OpenFlags.ReadWrite); store.Add(certificate);
                }
            }
            ValidatePackage(root, false);
            var pnp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");
            var inf = Path.Combine(root, "driver", "YomadoDriver.inf");
            var result = await RunAsync(pnp, "/add-driver", inf);
            if (result == 3010) return DriverInstallResult.RestartRequired;
            if (result is not (0 or 259)) return DriverInstallResult.StageFailed;
            if (await RunAsync(Path.Combine(root, "driver", "YomadoDevice.exe"), "--install") != 0) return DriverInstallResult.RegistrationFailed;
            result = await RunAsync(pnp, "/add-driver", inf, "/install");
            if (result == 3010) return DriverInstallResult.RestartRequired;
            // PnPUtil returns ERROR_NO_MORE_ITEMS when the existing device
            // already has the current package. Still confirm protocol readiness below.
            if (result is not (0 or 259)) return DriverInstallResult.BindingFailed;
            for (var retry = 0; retry < 100; retry++)
            {
                health = DriverHealth.Inspect();
                if (health.IsReady) return DriverInstallResult.Success;
                await Task.Delay(100);
            }
            return DriverInstallResult.NotReady;
        }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException) { return DriverInstallResult.SignatureRejected; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DriverInstallResult.InvalidPackage; }
        catch (Exception) { return DriverInstallResult.Unexpected; }
        finally { foreach (var file in locks) file.Dispose(); }
    }

    private static async Task<int> RunAsync(string path, params string[] arguments)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("安装进程未启动。");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await Task.WhenAll(output, error);
        return process.ExitCode;
    }

    public static string Message(DriverInstallResult result) => result switch
    {
        DriverInstallResult.Success => "驱动安装成功，可以创建虚拟屏了。",
        DriverInstallResult.Cancelled => "已取消安装。需要时可再次点击一键安装。",
        DriverInstallResult.AdminRequired => "安装需要管理员权限，请在 Windows 权限提示中选择“是”。",
        DriverInstallResult.InvalidPackage => "安装文件缺失或无法读取，请重新解压完整 Windows 发布包。",
        DriverInstallResult.ScreensInUse => "请先关闭所有 Yomado 虚拟屏，再安装或修复驱动。",
        DriverInstallResult.SignatureRejected => "驱动包签名未通过验证，请重新获取完整发布包。",
        DriverInstallResult.RestartRequired => "Windows 要求重启电脑后完成驱动安装，请保存工作后手动重启。",
        DriverInstallResult.NotReady => "驱动已安装但尚未就绪，请检查设备管理器中的 Yomado 显示设备后重试。",
        DriverInstallResult.StageFailed => "Windows 未接受驱动包，请检查驱动签名和系统安装限制后重试。",
        DriverInstallResult.RegistrationFailed => "无法注册虚拟显示设备，请重试或检查设备管理器。",
        DriverInstallResult.BindingFailed => "驱动绑定失败，请重试或检查设备管理器。",
        _ => "驱动安装未完成，请重新检测状态后重试。"
    };
}
