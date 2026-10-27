using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Yomado.Core;

namespace Yomado.Windows;

public sealed class DriverClient : IVirtualDisplayDriver
{
    public static readonly Guid InterfaceGuid = new("bcb78c7b-40c0-47cc-8ea7-31a7d5224726");
    public const uint ProtocolVersion = 1;
    private const uint QueryCode = 0x0022E004, CreateCode = 0x0022E008, RemoveCode = 0x0022E00C;
    private SafeFileHandle? device;
    public bool IsAvailable => FindInterfaces().Count != 0;
    public string Status => IsAvailable ? "已检测到虚拟显示驱动" : "尚未安装或启动 yomado 显示驱动";

    public Task EnsureConnectedAsync() => ConnectAsync(CancellationToken.None);

    internal static (bool Ready, int ActiveScreens) Probe()
    {
        var interfaces = FindInterfaces();
        if (interfaces.Count != 1) throw new InvalidOperationException("驱动接口尚不可用或存在重复设备。");
        using var client = new DriverClient();
        client.device = Native.CreateFile(interfaces[0], 0xC0000000, 3, 0, 3, 0, 0);
        if (client.device.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var state = client.Send(QueryCode, Request(0), 12 + DisplayProfile.MaximumDisplays * 16);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(8));
        if (count != DisplayProfile.MaximumDisplays) throw new InvalidDataException("驱动协议不匹配。");
        var active = Enumerable.Range(0, DisplayProfile.MaximumDisplays).Count(slot => BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(16 + slot * 16)) != 0);
        return (BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(4)) == 1, active);
    }

    private static IReadOnlyList<string> FindInterfaces()
    {
        var paths = new List<string>();
        var set = Native.SetupDiGetClassDevs(InterfaceGuid, 0, 0, 0x12);
        if (set == -1) return paths;
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new Native.InterfaceData { Size = (uint)Marshal.SizeOf<Native.InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, 0, InterfaceGuid, index, ref data)) break;
                Native.SetupDiGetDeviceInterfaceDetail(set, ref data, 0, 0, out var needed, 0);
                var detail = Marshal.AllocHGlobal((int)needed);
                try
                {
                    Marshal.WriteInt32(detail, nint.Size == 8 ? 8 : 6);
                    if (Native.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, needed, out _, 0))
                        paths.Add(Marshal.PtrToStringUni(detail + 4)!);
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return paths;
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (device is { IsInvalid: false, IsClosed: false }) return;
        if (FindInterfaces().Count == 0)
            throw new InvalidOperationException("请先安装签名后的 yomado 显示驱动，再创建虚拟屏。");
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var interfaces = FindInterfaces();
            if (interfaces.Count > 1) throw new InvalidOperationException("检测到多个 yomado 显示设备，请先移除重复设备。");
            if (interfaces.Count == 1)
            {
                device = Native.CreateFile(interfaces[0], 0xC0000000, 3, 0, 3, 0, 0);
                if (device.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法连接显示驱动。");
                for (var ready = 0; ready < 60; ready++)
                {
                    var state = Send(QueryCode, Request(0), 12 + DisplayProfile.MaximumDisplays * 16);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(4)) == 1) return;
                    await Task.Delay(100, cancellationToken);
                }
                throw new InvalidOperationException("显示驱动初始化超时，请查看设备管理器。");
            }
            await Task.Delay(100, cancellationToken);
        }
        throw new InvalidOperationException("未找到可用的 yomado 显示驱动。请先安装签名后的驱动包。");
    }

    private static byte[] Request(uint slot, uint width = 0, uint height = 0)
    {
        var input = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(input, ProtocolVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(4), slot);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), width);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(12), height);
        return input;
    }

    private byte[] Send(uint code, byte[] request, int capacity = 4)
    {
        if (device is null || device.IsInvalid || device.IsClosed) throw new InvalidOperationException("显示驱动尚未连接。");
        var output = new byte[capacity];
        if (!Native.DeviceIoControl(device, code, request, (uint)request.Length, output, (uint)output.Length, out var returned, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "显示驱动操作失败。");
        if (returned != capacity || BinaryPrimitives.ReadUInt32LittleEndian(output) != ProtocolVersion)
            throw new InvalidDataException("显示驱动与应用版本不匹配，请安装对应版本。");
        return output;
    }

    public async Task CreateAsync(DisplayProfile profile, CancellationToken cancellationToken = default)
    {
        if (!profile.IsValid) throw new ArgumentException("屏幕配置无效。");
        await ConnectAsync(cancellationToken);
        Send(CreateCode, Request(profile.Slot, (uint)profile.PixelWidth, (uint)profile.PixelHeight));
    }

    public void Remove(uint slot) => Send(RemoveCode, Request(slot));

    public IReadOnlyList<uint> GetConnectedSlots()
    {
        if (device is null || device.IsInvalid || device.IsClosed) return [];
        var state = Send(QueryCode, Request(0), 12 + DisplayProfile.MaximumDisplays * 16);
        if (BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(8)) != DisplayProfile.MaximumDisplays)
            throw new InvalidDataException("显示驱动屏幕数量不匹配。");
        return Enumerable.Range(0, DisplayProfile.MaximumDisplays).Where(slot =>
            BinaryPrimitives.ReadUInt32LittleEndian(state.AsSpan(12 + slot * 16 + 4)) != 0).Select(slot => (uint)slot).ToArray();
    }

    public void Dispose()
    {
        device?.Dispose(); device = null;
    }
}
