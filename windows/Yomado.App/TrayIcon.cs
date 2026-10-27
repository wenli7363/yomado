using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Yomado.App;

internal sealed class TrayIcon : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyData
    {
        public uint Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    private readonly HwndSource source;
    private NotifyData data;
    private readonly Action show, menu;
    private readonly uint taskbarCreated;
    public bool IsVisible { get; private set; }
    public TrayIcon(nint window, Action show, Action menu)
    {
        this.show = show; this.menu = menu;
        source = HwndSource.FromHwnd(window)!; source.AddHook(Message);
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        data = new() { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = window, Id = 1, Flags = 1 | 2 | 4,
            Callback = 0x8001, Icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "Yomado.ico"), 1, 32, 32, 0x10),
            Tip = "yomado · 夜窗", Info = "", InfoTitle = "", Version = 4 };
        IsVisible = ShellNotify(0, ref data);
        if (IsVisible) ShellNotify(4, ref data);
    }
    private nint Message(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == taskbarCreated) { IsVisible = ShellNotify(0, ref data); if (IsVisible) ShellNotify(4, ref data); }
        if (message != 0x8001) return 0;
        var evt = (int)((long)lParam & 0xffff);
        if (evt is 0x202 or 0x203 or 0x400 or 0x401) show();
        else if (evt is 0x205 or 0x7b) menu();
        handled = true; return 0;
    }
    public void Dispose() { ShellNotify(2, ref data); if (data.Icon != 0) DestroyIcon(data.Icon); source.RemoveHook(Message); }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotify(uint command, ref NotifyData data);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint module, string name, uint kind, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
}
