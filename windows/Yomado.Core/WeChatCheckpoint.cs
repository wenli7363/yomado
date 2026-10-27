namespace Yomado.Core;

public sealed record WeChatCheckpoint(long Handle, int ProcessId, long ProcessStart, string ApplicationPath,
    int Token, string? PhysicalDisplayId, PixelRect OriginalFrame, PixelRect? RelativeFrame,
    bool Maximized, string VirtualDisplayId, double OriginalScale = 1)
{
    public bool IsValid => Handle > 0 && ProcessId > 0 && ProcessStart > 0 && Token > 0 &&
        !string.IsNullOrWhiteSpace(ApplicationPath) && ApplicationPath.Length <= 32768 &&
        !string.IsNullOrWhiteSpace(VirtualDisplayId) && VirtualDisplayId.Length <= 1024 && OriginalFrame.IsUsable &&
        (RelativeFrame is null || RelativeFrame.Value.IsUsable) && double.IsFinite(OriginalScale) && OriginalScale is >= 0.5 and <= 8;
}
