namespace Yomado.Core;

public sealed record PointerBoundaryStatus(bool IsActive, string? Error, bool LeaseBypassActive, int DisplayCount,
    long ObservedMoves, long InjectedMoves, long ConstrainedMoves, long CorrectionFailures, long RecoveryMoves = 0,
    bool ClipApplied = false, PixelRect? ClipBounds = null, PixelRect? CurrentClip = null, string? ClipError = null);
