using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yomado.Core;

namespace Yomado.Windows;

// Geometry only. Window properties give a live identity that survives Yomado
// restarting but disappears when the actual WeChat window is destroyed.
public sealed class WeChatRecovery
{
    private const string Property = "Yomado.WeChat.Recovery.v1";
    private readonly IDesktop desktop;
    private readonly string path;
    private List<WeChatCheckpoint> checkpoints = [];
    private bool automaticPaused;
    public string? Status { get; private set; }
    public string? TakeStatus() { var status = Status; Status = null; return status; }
    public WeChatRecovery(IDesktop desktop, string path)
    {
        this.desktop = desktop; this.path = path;
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 131072) throw new InvalidDataException("微信恢复检查点过大，原文件已保留。");
        try { checkpoints = JsonSerializer.Deserialize<List<WeChatCheckpoint>>(File.ReadAllText(path)) ?? throw new JsonException(); }
        catch (JsonException ex) { throw new InvalidDataException("微信恢复检查点损坏，原文件已保留。", ex); }
        if (checkpoints.Count > 64 || checkpoints.Any(c => !c.IsValid) || checkpoints.DistinctBy(c => c.Handle).Count() != checkpoints.Count)
            throw new InvalidDataException("微信恢复检查点无效，原文件已保留。");
    }
    public static bool IsWeChat(string applicationPath) => Path.GetFileName(applicationPath).Equals("WeChat.exe", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(applicationPath).Equals("Weixin.exe", StringComparison.OrdinalIgnoreCase);

    public void EnsureMoveAllowed(string applicationPath)
    {
        PruneClosed();
        if (IsWeChat(applicationPath) && checkpoints.Count > 0)
            throw new InvalidOperationException("请先将微信返回原实体屏，再移入其他虚拟屏。");
    }
    public void BeforeMove(WindowOrigin origin)
    {
        if (!IsWeChat(origin.Window.ApplicationPath)) return;
        if (checkpoints.Count >= 64) throw new InvalidOperationException("微信恢复记录已满，请先返回已移入的窗口。");
        if (origin.PhysicalDisplayId is null || origin.RelativeFrame is null)
            throw new InvalidOperationException("请先将微信返回实体屏，以便保存安全恢复位置。");
        var token = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        if (!SetProp(origin.Window.Handle, Property, token)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法为微信建立恢复标记，尚未移屏。");
        var record = new WeChatCheckpoint((long)origin.Window.Handle, origin.Window.ProcessId, origin.Window.ProcessStart,
            origin.Window.ApplicationPath, token, origin.PhysicalDisplayId, origin.OriginalFrame, origin.RelativeFrame,
            origin.WasMaximized, origin.LastDisplayId, origin.OriginalScale);
        if (!record.IsValid) { RemoveProp(origin.Window.Handle, Property); throw new InvalidDataException("微信窗口恢复信息无效，尚未移屏。"); }
        checkpoints.Add(record);
        try { Save(); }
        catch { checkpoints.Remove(record); RemoveProp(origin.Window.Handle, Property); throw; }
    }
    private WindowInfo? Live(WeChatCheckpoint record)
    {
        var window = desktop.GetWindow((nint)record.Handle);
        return window is not null && window.ProcessId == record.ProcessId && window.ProcessStart == record.ProcessStart &&
            string.Equals(window.ApplicationPath, record.ApplicationPath, StringComparison.OrdinalIgnoreCase) &&
            GetProp(window.Handle, Property) == record.Token ? window : null;
    }
    private void PruneClosed()
    {
        var removed = checkpoints.RemoveAll(record => Live(record) is null);
        if (removed > 0) Save();
    }
    public void RecoverDisplay(string displayId)
    {
        PruneClosed();
        foreach (var checkpoint in checkpoints.Where(c => c.VirtualDisplayId == displayId).ToArray()) Recover(checkpoint);
        automaticPaused = false;
        Status = null;
    }
    public void RecoverApplication(string applicationPath)
    {
        PruneClosed();
        foreach (var checkpoint in checkpoints.Where(c => string.Equals(c.ApplicationPath, applicationPath, StringComparison.OrdinalIgnoreCase)).ToArray()) Recover(checkpoint);
        automaticPaused = false;
        Status = null;
    }
    public void Poll()
    {
        PruneClosed();
        if (automaticPaused) return;
        var displays = desktop.GetDisplays();
        foreach (var checkpoint in checkpoints.ToArray())
        {
            var window = Live(checkpoint); if (window is null) continue;
            var owner = GeometryPolicy.Owner(window.Frame, displays);
            if (owner is { IsVirtual: false } || !displays.Any(d => d.Id == checkpoint.VirtualDisplayId) || HasNativeDialog(window.ProcessId))
            {
                try { Recover(checkpoint); Status = "微信已返回原实体屏。"; }
                catch (Exception ex) { automaticPaused = true; Status = "微信自动恢复已暂停：" + ex.Message; return; }
            }
        }
    }
    private void Recover(WeChatCheckpoint checkpoint)
    {
        var window = Live(checkpoint); if (window is null) { checkpoints.Remove(checkpoint); Save(); return; }
        var target = GeometryPolicy.Physical(desktop.GetDisplays(), checkpoint.PhysicalDisplayId)
            ?? throw new InvalidOperationException("没有可用的实体屏；微信检查点已保留。");
        var relative = target.Id == checkpoint.PhysicalDisplayId && checkpoint.RelativeFrame.HasValue ?
            GeometryPolicy.Rescale(checkpoint.RelativeFrame.Value, checkpoint.OriginalScale, target.Scale, true) : (PixelRect?)null;
        var destination = GeometryPolicy.Place(GeometryPolicy.Rescale(checkpoint.OriginalFrame, checkpoint.OriginalScale, target.Scale), target, relative);
        if (!desktop.Place(window, destination, checkpoint.Maximized)) throw new InvalidOperationException("无法确认微信已返回，虚拟屏和检查点已保留。");
        checkpoints.Remove(checkpoint); Save(); RemoveProp(window.Handle, Property);
    }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(checkpoints, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
    private static bool HasNativeDialog(int processId)
    {
        var found = false;
        Native.WindowCallback enumerate = (handle, _) =>
        {
            if (!Native.IsWindowVisible(handle) || Native.GetWindowThreadProcessId(handle, out var pid) == 0 || pid != processId) return true;
            var className = new StringBuilder(256); GetClassName(handle, className, className.Capacity);
            var name = className.ToString();
            if ((name == "#32770" && Native.GetWindow(handle, 4) != 0) || name == "WeChatLoginWndForPC") { found = true; return false; }
            return true;
        };
        Native.EnumWindows(enumerate, 0); return found;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetProp(nint window, string name, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetProp(nint window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int length);
}
