using System.Runtime.InteropServices;

namespace Yomado.Windows;

public static class Authenticode
{
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo { public uint Size; public nint Path, File, Subject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        public uint Size; public nint Policy, Sip; public uint Ui, Revocation, Choice; public nint File;
        public uint Action; public nint State, Url; public uint Flags, Context; public nint Settings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(nint window, in Guid action, ref TrustData data);
    public static int Verify(string path)
    {
        var text = Marshal.StringToCoTaskMemUni(Path.GetFullPath(path));
        var file = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        var action = new Guid("00aac56b-cd44-11d0-8cc2-00c04fc295ee");
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), Ui = 2, Choice = 1, File = file, Action = 1, Flags = 0x1000 | 0x2000 };
        try
        {
            Marshal.StructureToPtr(new FileInfo { Size = (uint)Marshal.SizeOf<FileInfo>(), Path = text }, file, false);
            return WinVerifyTrust(-1, action, ref data);
        }
        finally
        {
            data.Action = 2; WinVerifyTrust(-1, action, ref data);
            Marshal.FreeHGlobal(file); Marshal.FreeCoTaskMem(text);
        }
    }
}
