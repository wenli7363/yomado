using Microsoft.Win32;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Yomado.Core;

namespace Yomado.App;

// A visible permission workflow, not an input injector or OS-level input lock.
public sealed class InputLeaseService : IDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly string pipeName;
    private readonly InputLeaseCoordinator coordinator = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly DispatcherTimer timer;
    private readonly Dictionary<string, TaskCompletionSource<InputLeaseReply>> waiting = [];
    private InputLeaseWindow? prompt, indicator;
    private InputLeaseControlsSnapshot? controls;
    private bool started, disposed;

    public InputLeaseService(Dispatcher dispatcher) : this(dispatcher, InputLeaseCommand.PipeName) { }

    // Only an in-process smoke fixture can select another pipe. Production CLI
    // clients always use the current user's current-session endpoint.
    internal InputLeaseService(Dispatcher dispatcher, string pipeName)
    {
        this.dispatcher = dispatcher;
        this.pipeName = pipeName;
        timer = new(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => Refresh(), dispatcher);
        timer.Stop();
        coordinator.RequestCompleted += CompleteRequest;
        coordinator.LeaseEnded += LeaseEnded;
    }

    public void Start()
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started) return;
        started = true;
        timer.Start();
        SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        SystemEvents.SessionSwitch += SessionChanged;
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new(pipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                _ = ServeAsync(pipe);
                pipe = null; // ServeAsync now owns it, including a pending decision.
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { await Task.Delay(250, stopping.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally { pipe?.Dispose(); }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            InputLeaseReply? delivery = null;
            Task<InputLeaseReply>? exchangeReply = null;
            string? exchangeRequestID = null;
            try
            {
                using var reading = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                reading.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var line = await InputLeaseCommand.ReadBoundedLineAsync(reader, reading.Token).ConfigureAwait(false);
                var request = line is null ? null : JsonSerializer.Deserialize<InputLeaseWireRequest>(line, InputLeaseCommand.Json);
                var exchange = await dispatcher.InvokeAsync(() =>
                {
                    var reply = Handle(request);
                    return (Reply: reply, RequestID: reply.IsCompleted ? null : coordinator.Pending?.RequestID);
                });
                exchangeReply = exchange.Reply;
                exchangeRequestID = exchange.RequestID;
                if (exchange.RequestID is not null)
                {
                    using var peer = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    var disconnected = reader.ReadAsync(new char[1].AsMemory(), peer.Token).AsTask();
                    var first = await Task.WhenAny(exchange.Reply, disconnected).ConfigureAwait(false);
                    if (first == disconnected || disconnected.IsCompletedSuccessfully)
                    {
                        await dispatcher.InvokeAsync(() => CancelDisconnectedExchange(exchange.RequestID, exchange.Reply));
                        return;
                    }
                    peer.Cancel();
                    try { await disconnected.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
                delivery = await exchange.Reply.WaitAsync(stopping.Token).ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(delivery, InputLeaseCommand.Json).AsMemory(), stopping.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidOperationException)
            {
                if (!disposed && exchangeReply is not null)
                    await dispatcher.InvokeAsync(() => CancelDisconnectedExchange(exchangeRequestID, exchangeReply));
            }
        }
    }

    private void CancelDisconnectedExchange(string? requestID, Task<InputLeaseReply> reply)
    {
        if (requestID is not null && coordinator.Pending?.RequestID == requestID) coordinator.Decide(requestID, false);
        if (reply.IsCompletedSuccessfully && reply.Result is { Status: "granted" } granted && coordinator.Active?.LeaseID == granted.LeaseID)
            coordinator.Stop("client-disconnected");
    }

    private Task<InputLeaseReply> Handle(InputLeaseWireRequest? request)
    {
        dispatcher.VerifyAccess();
        Refresh();
        if (disposed) return Task.FromResult(new InputLeaseReply("not-running"));
        if (request is null) return Task.FromResult(new InputLeaseReply("invalid"));
        if (request.Action == "status") return Task.FromResult(coordinator.Status(request.LeaseID));
        if (request.Action == "release") return Task.FromResult(request.LeaseID is null ? new InputLeaseReply("invalid") : coordinator.Release(request.LeaseID));
        if (request.Action != "request" || request.Agent is null || request.Reason is null)
            return Task.FromResult(new InputLeaseReply("invalid"));
        var result = coordinator.Request(request.Agent, request.Reason, request.Seconds);
        if (result.Status != "pending") return Task.FromResult(result);
        var pending = coordinator.Pending!;
        var completion = new TaskCompletionSource<InputLeaseReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting.Add(pending.RequestID, completion);
        // Capture before showing any prompt. A normal release restores only this
        // same surviving foreground window, never a reused handle/process.
        controls = InputLeaseControlsSnapshot.Capture();
        try
        {
            prompt = InputLeaseWindow.Prompt(pending, () => Decide(pending.RequestID, true), () => Decide(pending.RequestID, false));
            prompt.Show(); prompt.Position(false); Refresh();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { coordinator.Decide(pending.RequestID, false); }
        return completion.Task;
    }

    private void Decide(string requestID, bool allow)
    {
        coordinator.Decide(requestID, allow);
        Refresh();
    }

    private void CompleteRequest(string requestID, InputLeaseReply result)
    {
        Close(ref prompt);
        if (result.Status == "granted")
        {
            try
            {
                indicator = InputLeaseWindow.Indicator(coordinator.Active!.Agent, () => coordinator.Stop());
                indicator.Show(); indicator.Position(true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                coordinator.Stop("notice-unavailable");
                result = new("denied", Message: "无法保持键鼠借用提示可见，未授予许可。");
            }
        }
        else { controls?.Dispose(); controls = null; }
        if (waiting.Remove(requestID, out var completion)) completion.TrySetResult(result);
    }

    private void LeaseEnded(InputLeaseReply reply)
    {
        Close(ref indicator);
        try { if (reply.Reason is "released" or "expired") controls?.Restore(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { controls?.Dispose(); controls = null; }
    }

    private void Refresh()
    {
        coordinator.Refresh();
        if (coordinator.Active is not null && (indicator is null || !indicator.IsVisible || indicator.WindowState == WindowState.Minimized))
            coordinator.Stop("notice-unavailable");
        if (coordinator.Active is not null && indicator is not null)
        {
            try { if (!InputLeaseNative.VisibleOnPhysical(indicator)) coordinator.Stop("notice-unavailable"); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            { coordinator.Stop("notice-unavailable"); }
        }
        if (prompt is not null) prompt.Detail.Text = $"{Math.Ceiling(coordinator.PromptRemainingSeconds):0} 秒内不回应将自动拒绝。";
        if (indicator is not null)
        {
            var remaining = TimeSpan.FromSeconds(Math.Ceiling(coordinator.LeaseRemainingSeconds));
            indicator.Detail.Text = $"{coordinator.Active?.Agent} · 剩余 {(int)remaining.TotalMinutes}:{remaining.Seconds:00}";
        }
    }

    private void DisplaysChanged(object? sender, EventArgs e) => dispatcher.BeginInvoke(() =>
    {
        if (disposed) return;
        try { prompt?.Position(false); indicator?.Position(true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { coordinator.Stop("display-unavailable"); if (coordinator.Pending is { } pending) coordinator.Decide(pending.RequestID, false); }
    });

    private void SessionChanged(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect or SessionSwitchReason.SessionLogoff)) return;
        dispatcher.BeginInvoke(() =>
        {
            if (disposed) return;
            coordinator.Stop("session-unavailable");
            if (coordinator.Pending is { } pending) coordinator.Decide(pending.RequestID, false);
        });
    }

    private static void Close(ref InputLeaseWindow? window)
    {
        var previous = window; window = null;
        if (previous is null) return;
        previous.AllowClose = true; previous.Close();
    }

    public void Dispose()
    {
        dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true;
        timer.Stop();
        SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
        SystemEvents.SessionSwitch -= SessionChanged;
        coordinator.Shutdown();
        Close(ref prompt); Close(ref indicator);
        controls?.Dispose(); controls = null;
        stopping.Cancel();
    }
}

internal sealed class InputLeaseWindow : Window
{
    internal TextBlock Detail { get; } = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) };
    internal bool AllowClose { get; set; }
    private Action? closeAction;
    private InputLeaseWindow(string title, double width)
    {
        Title = "yomado · 夜窗"; Width = width; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStyle = WindowStyle.ToolWindow;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        SetResourceReference(BackgroundProperty, "PanelBackground"); SetResourceReference(ForegroundProperty, "PanelForeground");
        var content = new StackPanel { Margin = new(20) }; Content = content;
        content.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        Closing += (_, e) =>
        {
            if (AllowClose) return;
            e.Cancel = true;
            // Close notifications cannot synchronously Close the same window.
            Dispatcher.BeginInvoke(() => closeAction?.Invoke());
        };
        SourceInitialized += (_, _) => InputLeaseNative.MakeNonActivating(this);
    }

    internal static InputLeaseWindow Prompt(InputLeasePrompt pending, Action allow, Action deny)
    {
        var window = new InputLeaseWindow($"「{pending.Agent}」请求使用你的鼠标键盘", 440) { closeAction = deny };
        var content = (StackPanel)window.Content;
        content.Children.Add(new TextBlock { Text = $"原因：{pending.Reason}", TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) });
        content.Children.Add(new TextBlock { Text = $"允许后最长 {pending.Seconds} 秒。主屏会持续显示提示，你可以随时停止。Agent 操作期间请先不要动鼠标键盘。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) });
        content.Children.Add(window.Detail);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 18, 0, 0) };
        var cancel = new Button { Content = "拒绝", Margin = new(0, 0, 10, 0) };
        var accept = new Button { Content = "允许" }; // No default button: a click explicitly grants permission.
        cancel.Click += (_, _) => deny(); accept.Click += (_, _) => allow();
        row.Children.Add(cancel); row.Children.Add(accept); content.Children.Add(row);
        return window;
    }

    internal static InputLeaseWindow Indicator(string agent, Action stop)
    {
        var window = new InputLeaseWindow("Agent 正在使用你的鼠标键盘", 370) { closeAction = stop };
        var content = (StackPanel)window.Content;
        window.Detail.Text = agent; content.Children.Add(window.Detail);
        var button = new Button { Content = "停止", HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        button.Click += (_, _) => stop(); content.Children.Add(button);
        return window;
    }

    internal void Position(bool corner) => InputLeaseNative.Position(this, corner);
}
