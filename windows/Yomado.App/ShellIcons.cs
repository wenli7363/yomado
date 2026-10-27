using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Yomado.App;

internal static class ShellIcons
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct FileInfo
    {
        public nint Icon; public int Index; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint SHGetFileInfo(string path, uint attributes, ref FileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHDefExtractIcon(string path, int index, uint flags, out nint largeIcon, nint smallIcon, uint size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    public static ImageSource? ForApplication(string path)
    {
        if (!System.IO.File.Exists(path)) return null;
        // Keep the highest embedded icon resolution. A 32 DIP tile needs 72
        // physical pixels on a 225% desktop; a small Shell icon cannot supply it.
        var result = SHDefExtractIcon(path, 0, 0, out var large, 0, 256);
        if (large != 0)
        {
            try { if (result == 0) return FromIcon(large); }
            finally { DestroyIcon(large); }
        }
        var info = new FileInfo { DisplayName = "", TypeName = "" };
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<FileInfo>(), 0x100) == 0 || info.Icon == 0) return null;
        try { return FromIcon(info.Icon); }
        finally { DestroyIcon(info.Icon); }
    }
    private static ImageSource FromIcon(nint icon)
    {
        var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        image.Freeze(); return image;
    }
}
