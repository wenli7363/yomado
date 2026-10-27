using Yomado.Core;

internal static class InputLeaseTests
{
    public static void Register(Action<string, Action> test)
    {
        static void Check(bool condition) { if (!condition) throw new Exception("Input lease policy assertion failed."); }
        test("input lease clamps duration without accepting nonfinite values", () =>
        {
            Check(InputLeaseCoordinator.ClampSeconds(null) == 120 && InputLeaseCoordinator.ClampSeconds(double.NaN) == 120);
            Check(InputLeaseCoordinator.ClampSeconds(-5) == 120 && InputLeaseCoordinator.ClampSeconds(1) == 10);
            Check(InputLeaseCoordinator.ClampSeconds(9999) == 600 && InputLeaseCoordinator.ClampSeconds(10.5) == 11);
        });
        test("input lease prompt text cannot introduce lines or bidi controls", () =>
        {
            Check(InputLeaseCoordinator.DisplayText("  Codex\r\n  允许\t\u202e  ", 40, "fallback") == "Codex 允许");
            Check(InputLeaseCoordinator.DisplayText("\uD800", 40, "fallback") == "fallback");
        });
        test("input lease truncation preserves composed characters", () =>
        {
            Check(InputLeaseCoordinator.DisplayText("👩‍💻👩‍💻👩‍💻", 2, "fallback") == "👩‍💻…");
        });
        test("input lease allows one request or borrower at a time", () =>
        {
            var c = new InputLeaseCoordinator(new LeaseClock());
            Check(c.Request("one", "test").Status == "pending");
            Check(c.Request("two", "test").Status == "busy");
            Check(c.Decide(c.Pending!.RequestID, true).Status == "granted");
            Check(c.Request("two", "test").Status == "busy");
        });
        test("input lease has no implicit grant", () =>
        {
            var clock = new LeaseClock(); var c = new InputLeaseCoordinator(clock);
            InputLeaseReply? completion = null; c.RequestCompleted += (_, reply) => completion = reply;
            c.Request("one", "test"); clock.Advance(60); c.Refresh();
            Check(completion?.Status == "timeout" && c.Active is null && c.Pending is null);
        });
        test("input lease rejects approval at the exact deadline without timer tick", () =>
        {
            var clock = new LeaseClock(); var c = new InputLeaseCoordinator(clock);
            c.Request("one", "test"); var request = c.Pending!.RequestID; clock.Advance(60);
            Check(c.Decide(request, true).Status == "timeout" && c.Active is null);
        });
        test("input lease wall clock rollback cannot extend prompt deadline", () =>
        {
            var clock = new LeaseClock(); var c = new InputLeaseCoordinator(clock);
            c.Request("one", "test"); var request = c.Pending!.RequestID;
            clock.WallOffset = TimeSpan.FromDays(-5); clock.Advance(61);
            Check(c.Decide(request, true).Status == "timeout");
        });
        test("input lease status expires before reporting active without timer tick", () =>
        {
            var clock = new LeaseClock(); var c = new InputLeaseCoordinator(clock);
            c.Request("one", "test", 10); var lease = c.Decide(c.Pending!.RequestID, true).LeaseID!;
            clock.Advance(10);
            Check(c.Status(lease) is { Status: "ended", Reason: "expired" } && c.Active is null);
        });
        test("input lease late release retains expired reason", () =>
        {
            var clock = new LeaseClock(); var c = new InputLeaseCoordinator(clock);
            c.Request("one", "test", 10); var lease = c.Decide(c.Pending!.RequestID, true).LeaseID!;
            clock.Advance(11); Check(c.Release(lease) is { Status: "ended", Reason: "expired" });
        });
        test("input lease ending is idempotent and records user stop", () =>
        {
            var c = new InputLeaseCoordinator(); var events = 0; c.LeaseEnded += _ => events++;
            c.Request("one", "test"); var lease = c.Decide(c.Pending!.RequestID, true).LeaseID!;
            c.Stop(); c.Stop();
            Check(events == 1 && c.Status(lease) is { Status: "ended", Reason: "stopped" });
        });
        test("input lease unknown tokens cannot release another borrower", () =>
        {
            var c = new InputLeaseCoordinator(); c.Request("one", "test"); var lease = c.Decide(c.Pending!.RequestID, true).LeaseID!;
            Check(c.Release(Guid.NewGuid().ToString("D")).Status == "unknown" && c.Status(lease).Status == "active");
        });
        test("input lease denied request does not block later independent request", () =>
        {
            var c = new InputLeaseCoordinator(); c.Request("one", "test");
            Check(c.Decide(c.Pending!.RequestID, false).Status == "denied");
            Check(c.Request("two", "test").Status == "pending");
        });
        test("input lease shutdown denies prompt and ends active lease", () =>
        {
            var c = new InputLeaseCoordinator(); InputLeaseReply? completion = null;
            c.RequestCompleted += (_, reply) => completion = reply;
            c.Request("one", "test"); c.Shutdown(); Check(completion?.Status == "denied");
            c.Request("two", "test"); var lease = c.Decide(c.Pending!.RequestID, true).LeaseID!; c.Shutdown();
            Check(c.Status(lease) is { Status: "ended", Reason: "quit" });
        });
        test("input lease grant remains usable despite forward wall clock change", () =>
        {
            var clock = new LeaseClock(); var c = new InputLeaseCoordinator(clock);
            c.Request("one", "test", 120); var lease = c.Decide(c.Pending!.RequestID, true).LeaseID!;
            clock.WallOffset = TimeSpan.FromDays(2); clock.Advance(5);
            Check(c.Status(lease) is { Status: "active", RemainingSeconds: 115 });
        });
    }

    private sealed class LeaseClock : TimeProvider
    {
        private long timestamp;
        internal TimeSpan WallOffset { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(timestamp) + WallOffset;
        internal void Advance(int seconds) => timestamp += seconds * 1000;
    }
}
