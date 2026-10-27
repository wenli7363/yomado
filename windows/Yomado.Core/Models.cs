namespace Yomado.Core;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);
    public int Bottom => checked(Y + Height);
    public bool IsUsable => Width > 0 && Height > 0 && Width <= 100000 && Height <= 100000 &&
        Math.Abs((long)X) <= 1000000 && Math.Abs((long)Y) <= 1000000;
    public long Intersection(PixelRect other) => !IsUsable || !other.IsUsable ? 0 :
        (long)Math.Max(0, Math.Min(Right, other.Right) - Math.Max(X, other.X)) *
        Math.Max(0, Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y));
    public bool Near(PixelRect other, int tolerance = 3) => Math.Abs((long)X - other.X) <= tolerance &&
        Math.Abs((long)Y - other.Y) <= tolerance && Math.Abs((long)Width - other.Width) <= tolerance &&
        Math.Abs((long)Height - other.Height) <= tolerance;
    public bool Contains(PixelRect other, int tolerance = 3) => IsUsable && other.IsUsable &&
        other.X >= X - tolerance && other.Y >= Y - tolerance &&
        other.Right <= Right + tolerance && other.Bottom <= Bottom + tolerance;
}

public sealed record DisplayInfo(string Id, string DeviceName, string Name, PixelRect Bounds,
    PixelRect WorkArea, bool IsPrimary, bool IsYomado, uint? Slot = null, double Scale = 1, bool IsExternalVirtual = false)
{
    public bool IsVirtual => IsYomado || IsExternalVirtual;
}

public sealed record DisplayProfile(uint Slot, string Name, int Width = 1920, int Height = 1080, bool HiDpi = false)
{
    public const int MaximumDisplays = 16;
    public int PixelWidth => checked(Width * (HiDpi ? 2 : 1));
    public int PixelHeight => checked(Height * (HiDpi ? 2 : 1));
    public bool IsValid => Slot < MaximumDisplays && !string.IsNullOrWhiteSpace(Name) && Name.Trim().Length <= 60 &&
        ((Width == 1920 && Height == 1080) || (Width == 2560 && Height == 1440));
    public string DisplayName => $"虚拟屏 {Slot + 1} · {Name}";
}

// Start time and PID prevent a recycled HWND from inheriting another process's origin.
public sealed record WindowInfo(nint Handle, int ProcessId, long ProcessStart, string ApplicationPath,
    string ApplicationName, PixelRect Frame, bool Maximized)
{
    public PixelRect NormalFrame { get; init; } = Frame;
    public long Generation { get; init; }
    public bool Minimized { get; init; }
}
public sealed record WindowOrigin(WindowInfo Window, string? PhysicalDisplayId, PixelRect OriginalFrame,
    PixelRect? RelativeFrame, bool WasMaximized, string LastDisplayId, double OriginalScale = 1);

public interface IDesktop
{
    IReadOnlyList<DisplayInfo> GetDisplays();
    IReadOnlyList<WindowInfo> GetWindows();
    WindowInfo? GetWindow(nint handle);
    bool Place(WindowInfo identity, PixelRect frame, bool maximize);
}

public interface IVirtualDisplayDriver : IDisposable
{
    bool IsAvailable { get; }
    string Status { get; }
    Task CreateAsync(DisplayProfile profile, CancellationToken cancellationToken = default);
    void Remove(uint slot);
    IReadOnlyList<uint> GetConnectedSlots();
}
