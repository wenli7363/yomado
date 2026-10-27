using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Yomado.Core;

public sealed record InputLeaseReply(string Status, string? LeaseID = null, string? Reason = null,
    int? Seconds = null, double? RemainingSeconds = null, long? ExpiresAt = null, string? Message = null,
    PointerBoundaryStatus? PointerBoundary = null)
{
    [JsonIgnore] public int ExitCode => Status is "granted" or "active" or "released" or "idle" ? 0 : Status == "not-running" ? 3 : 2;
}

public sealed record InputLeasePrompt(string RequestID, string Agent, string Reason, int Seconds);
public sealed record ActiveInputLease(string LeaseID, string Agent, int Seconds, long ExpiresAt);

// Single-threaded policy. The host serializes requests and UI actions on its UI
// dispatcher. Every decision checks a monotonic deadline; timers only refresh UI.
public sealed class InputLeaseCoordinator(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Dictionary<string, string> ended = new(StringComparer.Ordinal);
    private long promptStarted, leaseStarted;
    public const int DecisionSeconds = 60;
    public InputLeasePrompt? Pending { get; private set; }
    public ActiveInputLease? Active { get; private set; }
    public event Action<string, InputLeaseReply>? RequestCompleted;
    public event Action<InputLeaseReply>? LeaseEnded;

    public static int ClampSeconds(double? seconds) => !seconds.HasValue || !double.IsFinite(seconds.Value) || seconds <= 0
        ? 120 : (int)Math.Clamp(Math.Round(seconds.Value, MidpointRounding.AwayFromZero), 10, 600);

    public static string DisplayText(string? value, int maximum, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var clean = new StringBuilder();
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        var pendingSpace = false;
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (!Rune.TryGetRuneAt(element, 0, out var first)) { pendingSpace = clean.Length > 0; continue; }
            var category = Rune.GetUnicodeCategory(first);
            if (Rune.IsWhiteSpace(first) || category is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned)
            { pendingSpace = clean.Length > 0; continue; }
            if (pendingSpace) clean.Append(' ');
            pendingSpace = false;
            clean.Append(element);
        }
        if (clean.Length == 0) return fallback;
        var text = clean.ToString();
        var starts = StringInfo.ParseCombiningCharacters(text);
        return starts.Length <= maximum ? text : text[..starts[maximum - 1]] + "…";
    }

    public InputLeaseReply Request(string? agent, string? reason, double? seconds = null)
    {
        Refresh();
        if (Pending is not null || Active is not null) return new("busy", Message: "另一个 Agent 正在使用或申请鼠标键盘。");
        Pending = new(Guid.NewGuid().ToString("D"), DisplayText(agent, 40, "未命名 Agent"),
            DisplayText(reason, 160, "未说明原因"), ClampSeconds(seconds));
        promptStarted = time.GetTimestamp();
        return new("pending");
    }

    public InputLeaseReply Decide(string requestID, bool allow)
    {
        // Refresh may complete the pending request as timeout. A late click
        // cannot create a lease even when the UI timer has not run yet.
        var wasPending = Pending?.RequestID == requestID;
        Refresh();
        if (Pending?.RequestID != requestID) return new(wasPending ? "timeout" : "unknown");
        var prompt = Pending;
        Pending = null;
        InputLeaseReply result;
        if (!allow) result = new("denied");
        else
        {
            leaseStarted = time.GetTimestamp();
            var expires = time.GetUtcNow().AddSeconds(prompt.Seconds).ToUnixTimeSeconds();
            Active = new(Guid.NewGuid().ToString("D"), prompt.Agent, prompt.Seconds, expires);
            result = new("granted", Active.LeaseID, Seconds: prompt.Seconds, ExpiresAt: expires);
        }
        RequestCompleted?.Invoke(requestID, result);
        return result;
    }

    public InputLeaseReply Status(string? leaseID = null)
    {
        Refresh();
        if (leaseID is null) return new(Active is not null || Pending is not null ? "busy" : "idle");
        if (Active?.LeaseID == leaseID) return new("active", leaseID, RemainingSeconds: Math.Ceiling(LeaseRemainingSeconds));
        return ended.TryGetValue(leaseID, out var reason) ? new("ended", leaseID, reason) : new("unknown");
    }

    public InputLeaseReply Release(string leaseID)
    {
        Refresh();
        if (Active?.LeaseID != leaseID) return Status(leaseID);
        End("released");
        return new("released", leaseID);
    }

    public void Stop(string reason = "stopped") { Refresh(); End(reason); }

    public void Shutdown()
    {
        if (Pending is { } pending)
        {
            Pending = null;
            RequestCompleted?.Invoke(pending.RequestID, new("denied"));
        }
        End("quit");
    }

    public double PromptRemainingSeconds => Pending is null ? 0 : Math.Max(0,
        DecisionSeconds - time.GetElapsedTime(promptStarted).TotalSeconds);
    public double LeaseRemainingSeconds => Active is null ? 0 : Math.Max(0,
        Active.Seconds - time.GetElapsedTime(leaseStarted).TotalSeconds);

    public void Refresh()
    {
        if (Pending is { } pending && PromptRemainingSeconds <= 0)
        {
            Pending = null;
            RequestCompleted?.Invoke(pending.RequestID, new("timeout"));
        }
        if (Active is not null && LeaseRemainingSeconds <= 0) End("expired");
    }

    private void End(string reason)
    {
        if (Active is not { } active) return;
        Active = null;
        if (ended.Count >= 64) ended.Remove(ended.Keys.First());
        ended[active.LeaseID] = reason;
        LeaseEnded?.Invoke(new("ended", active.LeaseID, reason));
    }
}
