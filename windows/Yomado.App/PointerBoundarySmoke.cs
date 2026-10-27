using System.IO;
using System.Text.Json;
using System.Windows;
using Yomado.Windows;

namespace Yomado.App;

internal static class PointerBoundarySmoke
{
    // Keep the display snapshot empty: registration/cleanup are exercised,
    // but every real mouse event passes through without any cursor writes.
    public static void Run(string reportPath)
    {
        var registered = false;
        var removed = false;
        string? error = null;
        try
        {
            using var boundary = new VirtualDisplayPointerBoundary();
            registered = boundary.IsActive;
            if (!registered) throw new InvalidOperationException(boundary.Error ?? "鼠标钩子未注册。");
            boundary.UpdateDisplays([]);
            boundary.AllowVirtualInputFor(TimeSpan.FromSeconds(10));
            if (!boundary.GetStatus().LeaseBypassActive)
                throw new InvalidOperationException("空快照的线程解除确认未完成。");
            boundary.AllowVirtualInputFor(TimeSpan.Zero);
            // Let the independent native timer run with no managed display.
            Thread.Sleep(50);
            var status = boundary.GetStatus();
            if (status.Error is not null) throw new InvalidOperationException(status.Error);
            if (status.LeaseBypassActive || status.DisplayCount != 0 || status.ConstrainedMoves != 0 || status.CorrectionFailures != 0 || status.RecoveryMoves != 0 ||
                status.ClipApplied || status.ClipBounds is not null || status.CurrentClip is not null || status.ClipError is not null)
                throw new InvalidOperationException("空显示快照的只读检查产生了鼠标修正。");
            boundary.Dispose();
            removed = !boundary.IsActive;
            if (!removed) throw new InvalidOperationException("鼠标钩子未退出。");
        }
        catch (Exception ex) { error = ex.Message; }
        var fullPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(new { passed = error is null, registered, removed, error },
            new JsonSerializerOptions { WriteIndented = true }));
        Application.Current.Shutdown(error is null ? 0 : 1);
    }
}
