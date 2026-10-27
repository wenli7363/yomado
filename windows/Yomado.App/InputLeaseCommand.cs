using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yomado.Core;

namespace Yomado.App;

internal sealed record InputLeaseWireRequest(string Action, string? Agent = null, string? Reason = null,
    double? Seconds = null, string? LeaseID = null);

public static class InputLeaseCommand
{
    internal static readonly JsonSerializerOptions Json = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    internal static string PipeName
    {
        get
        {
            var identity = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("当前用户不可用。");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
            return $"Yomado.InputLease.{hash}.{Process.GetCurrentProcess().SessionId}";
        }
    }

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] is
        "--input" or "--input-request" or "--input-status" or "--input-release";

    // Run before the application's single-instance activation path: a lease
    // query must not activate the control panel or change the foreground app.
    public static async Task<int> RunAsync(string[] args)
    {
        InputLeaseWireRequest request;
        try { request = Parse(args); }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("Usage: Yomado --input request --agent <name> --reason <text> [--seconds <10-600>]\n       Yomado --input status [--lease <id>]\n       Yomado --input release --lease <id>\n       Yomado --input pointer-status");
            return 64;
        }
        InputLeaseReply reply;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.Action == "request" ? 68 : 5));
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(3000, timeout.Token);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, Json).AsMemory(), timeout.Token);
            var line = await ReadBoundedLineAsync(reader, timeout.Token);
            reply = line is null ? new("not-running", Message: "yomado 未回应。") :
                JsonSerializer.Deserialize<InputLeaseReply>(line, Json) ?? new("not-running");
        }
        catch (OperationCanceledException) { reply = new("timeout"); }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or JsonException)
        { reply = new("not-running", Message: "yomado 未运行或不支持键鼠借用。"); }
        Console.WriteLine(JsonSerializer.Serialize(reply, Json));
        return reply.ExitCode;
    }

    private static InputLeaseWireRequest Parse(string[] args)
    {
        if (!IsCommand(args)) throw new ArgumentException();
        var canonical = args[0] == "--input";
        if (canonical && args.Length < 2) throw new ArgumentException();
        var action = canonical ? args[1] : args[0][8..];
        if (action is not ("request" or "status" or "release" or "pointer-status")) throw new ArgumentException();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = canonical ? 2 : 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--agent" or "--reason" or "--seconds" or "--lease") ||
                !options.TryAdd(args[i], args[i + 1])) throw new ArgumentException();
        }
        var allowed = action == "request" ? new[] { "--agent", "--reason", "--seconds" } :
            action == "pointer-status" ? [] : new[] { "--lease" };
        if (options.Keys.Any(k => !allowed.Contains(k))) throw new ArgumentException();
        double? seconds = null;
        if (options.TryGetValue("--seconds", out var raw))
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || !double.IsFinite(parsed))
                throw new ArgumentException();
            seconds = parsed;
        }
        if (action == "request" && (!options.ContainsKey("--agent") || !options.ContainsKey("--reason"))) throw new ArgumentException();
        options.TryGetValue("--lease", out var lease);
        if (lease is not null && !Guid.TryParseExact(lease, "D", out _)) throw new ArgumentException();
        if (action == "release" && lease is null) throw new ArgumentException();
        return new(action, options.GetValueOrDefault("--agent"), options.GetValueOrDefault("--reason"), seconds, lease);
    }

    internal static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var next = new char[1];
        while (await reader.ReadAsync(next.AsMemory(), cancellationToken) != 0)
        {
            if (next[0] == '\n') return result.ToString().TrimEnd('\r');
            if (result.Length >= 4096) throw new IOException("键鼠借用消息过长。");
            result.Append(next[0]);
        }
        return result.Length == 0 ? null : result.ToString();
    }
}
