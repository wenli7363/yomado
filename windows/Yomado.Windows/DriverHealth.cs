using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Yomado.Windows;

public enum DriverCondition { Missing, Starting, Ready, NeedsRepair, Duplicate }
public sealed record DriverHealth(DriverCondition Condition, string Message, int ActiveScreens = 0, uint ProblemCode = 0)
{
    public bool IsReady => Condition == DriverCondition.Ready;
    public static DriverHealth Inspect()
    {
        var displayClass = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
        var set = Native.SetupDiGetClassDevs(displayClass, 0, 0, 2);
        if (set == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
        var devices = new List<(uint Status, uint Problem)>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var device = new Native.DeviceData { Size = (uint)Marshal.SizeOf<Native.DeviceData>() };
                if (!Native.SetupDiEnumDeviceInfo(set, index, ref device))
                {
                    if (Marshal.GetLastWin32Error() != 259) throw new Win32Exception(Marshal.GetLastWin32Error());
                    break;
                }
                var ids = new byte[4096];
                if (!Native.SetupDiGetDeviceRegistryProperty(set, ref device, 1, out var type, ids, (uint)ids.Length, out var needed) || type != 7) continue;
                if (!Encoding.Unicode.GetString(ids, 0, (int)Math.Min(needed, (uint)ids.Length)).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Contains("Root\\YomadoVirtualDisplay", StringComparer.OrdinalIgnoreCase)) continue;
                var result = Native.CM_Get_DevNode_Status(out var status, out var problem, device.Instance, 0);
                if (result != 0) return new(DriverCondition.Starting, "正在等待显示驱动启动…");
                devices.Add((status, problem));
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        if (devices.Count == 0) return new(DriverCondition.Missing, "尚未安装虚拟显示驱动");
        if (devices.Count > 1) return new(DriverCondition.Duplicate, "检测到重复驱动设备，请先在设备管理器中移除重复项");
        if (devices[0].Problem != 0) return new(DriverCondition.NeedsRepair, $"驱动启动异常（设备错误 {devices[0].Problem}）", ProblemCode: devices[0].Problem);
        if ((devices[0].Status & 8) == 0) return new(DriverCondition.NeedsRepair, "驱动设备尚未启动，可尝试重新安装");
        try
        {
            var probe = DriverClient.Probe();
            return probe.Ready ? new(DriverCondition.Ready, "虚拟显示驱动已就绪", probe.ActiveScreens) :
                new(DriverCondition.Starting, "显示驱动正在初始化…", probe.ActiveScreens);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidDataException or InvalidOperationException)
        { return new(DriverCondition.NeedsRepair, "驱动连接或版本异常，可尝试重新安装"); }
    }
}
