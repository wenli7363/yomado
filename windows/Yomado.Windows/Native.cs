using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Yomado.Windows;

internal static class Native
{
    internal const uint QdcActive = 2;
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly Core.PixelRect ToPixels() => new(Left, Top, Right - Left, Bottom - Top);
        public static Rect From(Core.PixelRect r) => new() { Left = r.X, Top = r.Y, Right = r.Right, Bottom = r.Bottom };
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MonitorInfo
    {
        public uint Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Placement
    { public uint Length, Flags, ShowCmd; public Point MinPosition, MaxPosition; public Rect Normal; }
    [StructLayout(LayoutKind.Sequential)] internal struct SourceInfo
    { public Luid Adapter; public uint Id, ModeIndex, Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rational { public uint Numerator, Denominator; }
    [StructLayout(LayoutKind.Sequential)] internal struct TargetInfo
    {
        public Luid Adapter; public uint Id, ModeIndex, Technology, Rotation, Scaling;
        public Rational Refresh; public uint Scanline, Available, Flags;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct DisplayPath { public SourceInfo Source; public TargetInfo Target; public uint Flags; }
    [StructLayout(LayoutKind.Explicit, Size = 64)] internal struct ModeInfo
    {
        [FieldOffset(0)] public uint Type; [FieldOffset(4)] public uint Id; [FieldOffset(8)] public Luid Adapter;
        [FieldOffset(16)] public uint Width; [FieldOffset(20)] public uint Height;
        [FieldOffset(24)] public uint PixelFormat; [FieldOffset(28)] public Point Position;
        // Cover the complete native union, including target video timing.
        [FieldOffset(16)] public ulong Raw0; [FieldOffset(24)] public ulong Raw1;
        [FieldOffset(32)] public ulong Raw2; [FieldOffset(40)] public ulong Raw3;
        [FieldOffset(48)] public ulong Raw4; [FieldOffset(56)] public ulong Raw5;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct DeviceHeader { public uint Type, Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct SourceName
    { public DeviceHeader Header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct TargetName
    {
        public DeviceHeader Header; public uint Flags, Technology; public ushort Manufacturer, Product; public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Path;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { public uint Size; public Guid Class; public uint Flags; public nuint Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct DeviceData { public uint Size; public Guid Class; public uint Instance; public nuint Reserved; }
    internal delegate bool MonitorCallback(nint monitor, nint dc, nint rect, nint data);
    internal delegate bool WindowCallback(nint window, nint data);
    internal delegate void WinEventCallback(nint hook, uint evt, nint window, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumDisplayMonitors(nint dc, nint rect, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("shcore.dll")] internal static extern int GetScaleFactorForMonitor(nint monitor, out uint scale);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] internal static extern nint GetWindowDpiAwarenessContext(nint window);
    [DllImport("user32.dll")] internal static extern int GetAwarenessFromDpiAwarenessContext(nint context);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] internal static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] DisplayPath[] pathArray, ref uint modes, [Out] ModeInfo[] modeArray, nint topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] internal static extern int GetSourceName(ref SourceName name);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] internal static extern int GetTargetName(ref TargetName name);
    [DllImport("user32.dll")] internal static extern int SetDisplayConfig(uint paths, DisplayPath[] pathArray, uint modes, ModeInfo[] modeArray, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsZoomed(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint window, System.Text.StringBuilder name, int capacity);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowPlacement(nint window, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPlacement(nint window, in Placement placement);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint window, uint attribute, out uint value, uint size);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("kernel32.dll")] internal static extern uint SetThreadExecutionState(uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, uint inputLength, byte[] output, uint outputLength, out uint returned, nint overlapped);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint SetupDiGetClassDevs(in Guid classGuid, nint enumerator, nint window, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInterfaces(nint set, nint device, in Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref DeviceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceRegistryProperty(nint set, ref DeviceData data, uint property, out uint type, byte[] buffer, uint size, out uint needed);
    [DllImport("cfgmgr32.dll")] internal static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint instance, uint flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiGetDeviceInterfaceDetail(nint set, ref InterfaceData data, nint detail, uint size, out uint needed, nint info);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetupDiDestroyDeviceInfoList(nint set);
}
