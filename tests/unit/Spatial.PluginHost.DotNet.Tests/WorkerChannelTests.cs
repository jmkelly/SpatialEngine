using System.Text.Json.Nodes;
using Spatial.PluginHost.DotNet.Protocol;

namespace Spatial.PluginHost.DotNet.Tests;

/// <summary>
/// The shared channel (ADR-0025): request/response correlation,
/// fire-and-forget routing, timeout/cancellation, protocol-error reporting,
/// bounded lines and the guarantee that a disconnect fails every outstanding
/// request instead of hanging it.
/// </summary>
public sealed class WorkerChannelTests : IAsyncDisposable
{
    private readonly LoopbackPair _loop = new();
    private readonly List<WorkerChannel> _channels = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var channel in _channels)
        {
            await channel.DisposeAsync();
        }

        _loop.Dispose();
    }

    [Fact]
    public async Task Request_gets_its_correlated_response()
    {
        var (a, b) = CreatePair(b => EchoAs(b, WorkerProtocol.Pong));

        var payload = new JsonObject { ["nonce"] = "n1" };
        var response = await a.RequestAsync(WorkerProtocol.Ping, payload, WorkerProtocol.Pong, TimeSpan.FromSeconds(2));

        Assert.Equal("n1", response!["nonce"]!.GetValue<string>());
    }

    [Fact]
    public async Task Fire_and_forget_messages_route_to_the_handler_in_order()
    {
        var received = new TaskCompletionSource<string>();
        var a = CreateChannel("a", static _ => ValueTask.CompletedTask);
        _ = CreateChannel("b", envelope =>
        {
            received.TrySetResult(envelope.Type);
            return ValueTask.CompletedTask;
        });

        await a.SendAsync(WorkerProtocol.Hello, new JsonObject { ["id"] = "fixture@1" });
        Assert.Equal(WorkerProtocol.Hello, await received.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task A_timeout_releases_the_caller_and_the_channel_survives()
    {
        var (a, b) = CreatePair(b => async envelope =>
        {
            if (envelope.Type == WorkerProtocol.Ping)
            {
                await Task.Delay(200);
                await b.SendAsync(WorkerProtocol.Pong, envelope.Payload?.DeepClone(), envelope.Id);
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            a.RequestAsync(WorkerProtocol.Ping, null, WorkerProtocol.Pong, TimeSpan.FromMilliseconds(10)).AsTask());
        await Task.Delay(300); // let the late answer arrive (and be ignored)

        Assert.True(a.IsConnected);
        Assert.True(b.IsConnected);
        await PingOkAsync(a);
    }

    [Fact]
    public async Task Disconnect_fails_every_outstanding_request()
    {
        var (a, b) = CreatePair(b => static _ => ValueTask.CompletedTask);

        var request = a.RequestAsync(WorkerProtocol.Ping, null, WorkerProtocol.Pong, timeout: null).AsTask();
        _loop.CompleteAInput(); // side B's writer ends: A reads EOF, its loop ends, pending requests fail
        await Task.Yield();

        var exception = await Assert.ThrowsAsync<WorkerDisconnectedException>(() => request);
        Assert.Contains("disconnected", exception.Message);
        Assert.False(a.IsConnected);
    }

    [Fact]
    public async Task A_wrong_response_type_is_a_protocol_violation()
    {
        var (a, b) = CreatePair(b => EchoAs(b, WorkerProtocol.Result)); // wrong type for a ping

        var exception = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
            a.RequestAsync(WorkerProtocol.Ping, null, WorkerProtocol.Pong, TimeSpan.FromSeconds(2)).AsTask());
        Assert.Contains("expected a 'pong' response but received 'result'", exception.Message);
    }

    [Fact]
    public async Task Malformed_input_is_reported_as_an_error_envelope()
    {
        var reported = new TaskCompletionSource<string>();
        _ = CreateChannel("a", static _ => ValueTask.CompletedTask);
        _ = CreateChannel("b", envelope =>
        {
            reported.TrySetResult(envelope.Payload?["error"]?["message"]?.GetValue<string>() ?? string.Empty);
            return ValueTask.CompletedTask;
        });

        // Feed A's input directly; A replies with an error envelope that B reads.
        var writer = new StreamWriter(_loop.BOutput) { AutoFlush = true };
        await writer.WriteLineAsync("{ not json");
        var message = await reported.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains("not valid JSON", message);
    }

    [Fact]
    public async Task Oversized_lines_disconnect_with_an_actionable_fault()
    {
        var (a, _) = CreatePair(b => static _ => ValueTask.CompletedTask);

        var request = a.RequestAsync(WorkerProtocol.Ping, null, WorkerProtocol.Pong, timeout: null).AsTask();
        var writer = new StreamWriter(_loop.BOutput) { AutoFlush = true };
        await writer.WriteLineAsync(new string('x', WorkerChannel.MaxLineBytes + 1));

        var exception = await Assert.ThrowsAsync<WorkerDisconnectedException>(() => request);
        Assert.Contains("byte limit", exception.Message);
        Assert.NotNull(a.Fault);
    }

    [Fact]
    public async Task Lines_split_across_os_reads_and_crlf_still_parse()
    {
        var received = new TaskCompletionSource<string>();
        _ = CreateChannel("a", static _ => ValueTask.CompletedTask);
        _ = CreateChannel("b", envelope =>
        {
            received.TrySetResult(envelope.Type);
            return ValueTask.CompletedTask;
        });

        // Feed B's input directly (B reads what A writes); split the line across several writes.
        var writer = new StreamWriter(_loop.AOutput) { AutoFlush = true };
        await writer.WriteAsync("{\"protocol\":\"spatial.wo");
        await Task.Delay(20);
        await writer.WriteAsync("rker/1\",\"type\":\"ping\",\"id\":\"x\"}\r\n{\"protocol\":\"spatial.worker");
        await Task.Delay(20);
        await writer.WriteLineAsync("/1\",\"type\":\"hello\"}");

        Assert.Equal(WorkerProtocol.Ping, await received.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task A_caller_can_cancel_an_outstanding_request()
    {
        var (a, b) = CreatePair(b => EchoAs(b, WorkerProtocol.Pong));
        using var caller = new CancellationTokenSource();

        var request = a.RequestAsync(WorkerProtocol.Ping, null, WorkerProtocol.Pong, timeout: null, caller.Token).AsTask();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(a.IsConnected);
        await PingOkAsync(a);
    }

    private static async Task PingOkAsync(WorkerChannel a)
    {
        var response = await a.RequestAsync(
            WorkerProtocol.Ping, new JsonObject { ["nonce"] = "again" }, WorkerProtocol.Pong, TimeSpan.FromSeconds(2));
        Assert.Equal("again", response!["nonce"]!.GetValue<string>());
    }

    private (WorkerChannel A, WorkerChannel B) CreatePair(Func<WorkerChannel, Func<WorkerEnvelope, ValueTask>> handlerForB)
    {
        var a = CreateChannel("a", static _ => ValueTask.CompletedTask);
        WorkerChannel? b = null;
        b = CreateChannel("b", envelope => handlerForB(b!)(envelope));
        return (a, b);
    }

    private static Func<WorkerEnvelope, ValueTask> EchoAs(WorkerChannel channel, string type) => envelope =>
    {
        if (envelope.Id is not null)
        {
            return channel.SendAsync(type, envelope.Payload?.DeepClone(), envelope.Id);
        }

        return ValueTask.CompletedTask;
    };

    private WorkerChannel CreateChannel(string name, Func<WorkerEnvelope, ValueTask> handler)
    {
        var channel = new WorkerChannel(
            name == "a" ? _loop.AInput : _loop.BInput,
            name == "a" ? _loop.AOutput : _loop.BOutput,
            handler,
            name);
        _channels.Add(channel);
        return channel;
    }
}
