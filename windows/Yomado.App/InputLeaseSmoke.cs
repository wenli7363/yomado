using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using Yomado.Core;

namespace Yomado.App;

public static class InputLeaseSmoke
{
    // Exercises the real IPC server on an isolated endpoint. Every operation
    // is read-only: no request, prompt, lease, native desktop call or input.
    public static async void Run(string reportPath)
    {
        var checks = new List<string>();
        string? error = null;
        try
        {
            var pipeName = $"Yomado.InputLease.Smoke.{Guid.NewGuid():N}";
            using var service = new InputLeaseService(Application.Current.Dispatcher, pipeName);
            service.Start();
            var idle = await Exchange(pipeName, new("status"));
            if (idle.Status != "idle" || idle.ExitCode != 0) throw new InvalidOperationException("Empty service did not report idle.");
            checks.Add("status returns idle over a same-user named pipe");
            var unknown = await Exchange(pipeName, new("release", LeaseID: Guid.NewGuid().ToString("D")));
            if (unknown.Status != "unknown" || unknown.ExitCode != 2) throw new InvalidOperationException("Unknown lease release was accepted.");
            checks.Add("unknown release does not create or change a lease");
            var invalid = await Exchange(pipeName, new("unsupported-action"));
            if (invalid.Status != "invalid") throw new InvalidOperationException("Invalid IPC command was accepted.");
            checks.Add("invalid IPC command is rejected");
            if ((await Exchange(pipeName, new("status"))).Status != "idle") throw new InvalidOperationException("Read-only commands changed lease state.");
            checks.Add("read-only commands leave the service idle");
        }
        catch (Exception ex) { error = ex.Message; }
        try
        {
            var fullPath = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, JsonSerializer.Serialize(new { passed = error is null, checks, error },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Application.Current.Shutdown(error is null ? 0 : 1); }
    }

    private static async Task<InputLeaseReply> Exchange(string pipeName, InputLeaseWireRequest request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(3000, timeout.Token);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, InputLeaseCommand.Json).AsMemory(), timeout.Token);
        var line = await InputLeaseCommand.ReadBoundedLineAsync(reader, timeout.Token);
        return line is not null ? JsonSerializer.Deserialize<InputLeaseReply>(line, InputLeaseCommand.Json) ?? throw new IOException("Empty lease reply.") :
            throw new IOException("IPC server disconnected without replying.");
    }
}
