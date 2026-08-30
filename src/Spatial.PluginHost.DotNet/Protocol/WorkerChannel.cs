using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace Spatial.PluginHost.DotNet.Protocol;

/// <summary>
/// One end of a worker wire connection: a background reader loop, a serialised
/// writer and request/response correlation over line-delimited JSON
/// (ADR-0025). Used symmetrically by the worker host (its channel is
/// stdin/stdout) and by the process supervisor (its channel is the child's
/// stdout/stdin). <see cref="RequestAsync"/> sends and awaits a response with
/// any timeout/cancellation; fire-and-forget messages flow through the
/// constructor's handler. A disconnect — clean EOF or a reader fault — fails
/// every outstanding request with <see cref="WorkerDisconnectedException"/>,
/// so a crashing worker can never leave a caller hanging.
/// </summary>
public sealed class WorkerChannel : IAsyncDisposable
{
    /// <summary>The maximum envelope line length; longer lines are a protocol error (host resource limits).</summary>
    public const int MaxLineBytes = 4 * 1024 * 1024;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Func<WorkerEnvelope, ValueTask> _onMessage;
    private readonly string _name;
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly LineReader _lines = new();
    private readonly Task _readLoop;
    private readonly TaskCompletionSource _disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _fault;
    private bool _disposed;

    /// <summary>
    /// Creates a channel and starts its reader loop. <paramref name="onMessage"/>
    /// receives every envelope that is not a correlated response; it runs
    /// sequentially in read order, so handshake and drain ordering is natural.
    /// </summary>
    public WorkerChannel(Stream input, Stream output, Func<WorkerEnvelope, ValueTask> onMessage, string name)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _onMessage = onMessage ?? throw new ArgumentNullException(nameof(onMessage));
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _readLoop = Task.Run(() => ReadLoopAsync(_lifetime.Token));
    }

    public string Name => _name;

    /// <summary>Fault that ended the reader loop (null for a clean peer EOF).</summary>
    public Exception? Fault => _fault;

    /// <summary>Completes when the peer disconnected or the channel was disposed.</summary>
    public Task Disconnected => _disconnected.Task;

    /// <summary>Sends one fire-and-forget envelope (no response expected).</summary>
    public ValueTask SendAsync(string type, JsonNode? payload = null, string? id = null)
    {
        ThrowIfGone();
        var line = WorkerWireCodec.Encode(new WorkerEnvelope(WorkerProtocol.Version, type, id, payload));
        return WriteLineAsync(line);
    }

    /// <summary>
    /// Sends a request and awaits the correlated response. The response must
    /// echo <paramref name="id"/> (a fresh one when omitted) with one of the
    /// protocol's response types; anything else is a protocol violation.
    /// Cancellation or the timeout release the caller (the pending entry is
    /// dropped), and the channel still survives — a late answer is reported
    /// to the message handler as an unknown-id envelope.
    /// </summary>
    public async ValueTask<JsonNode?> RequestAsync(
        string type,
        JsonNode? payload,
        string responseType,
        TimeSpan? timeout = null,
        string? id = null,
        CancellationToken cancellationToken = default)
    {
        id ??= Guid.NewGuid().ToString("N");
        var pending = new PendingRequest(id, responseType, _name);
        if (!_pending.TryAdd(id, pending))
        {
            throw new WorkerProtocolException($"duplicate request id {id}");
        }

        try
        {
            await SendAsync(type, payload, id);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout is { } duration)
            {
                linked.CancelAfter(duration);
            }

            return await pending.Completion.WaitAsync(linked.Token);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Whether the channel is still connected (its reader loop is alive).</summary>
    public bool IsConnected => !_disconnected.Task.IsCompleted;

    /// <summary>Stops the reader loop and fails every outstanding request. Does not dispose the streams (the owner does).</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        try
        {
            await _readLoop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
        }

        _disconnected.TrySetResult();
        _lifetime.Dispose();
        _writeGate.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _lines.ReadLineAsync(_input, MaxLineBytes, cancellationToken) is { } line)
            {
                await DispatchLineAsync(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _fault = exception;
        }
        finally
        {
            _disconnected.TrySetResult();
            foreach (var pending in _pending.Values)
            {
                pending.Fail(new WorkerDisconnectedException(
                    $"the worker channel '{_name}' disconnected while the request was outstanding"
                    + (_fault is null ? string.Empty : $": {_fault.Message}")));
            }

            _pending.Clear();
        }
    }

    /// <summary>
    /// Handles one complete wire line: empty lines are ignored, malformed JSON
    /// is answered with an error envelope, a correlated response resolves the
    /// matching pending request and anything else is delivered to the message
    /// handler in read order.
    /// </summary>
    private async ValueTask DispatchLineAsync(string line)
    {
        if (line.Length == 0)
        {
            return;
        }

        WorkerEnvelope envelope;
        try
        {
            envelope = WorkerWireCodec.Decode(line);
        }
        catch (WorkerProtocolException exception)
        {
            await TrySendErrorAsync(exception.Message);
            return;
        }

        if (TryConsumeResponse(envelope))
        {
            return;
        }

        await _onMessage(envelope);
    }

    /// <summary>Whether the envelope answers a pending request (and was consumed by it).</summary>
    private bool TryConsumeResponse(WorkerEnvelope envelope)
    {
        return envelope.Id is { } id
            && envelope.IsProtocolV1
            && _pending.TryGetValue(id, out var pending)
            && pending.ResolveOrReject(envelope);
    }

    private async ValueTask TrySendErrorAsync(string message)
    {
        try
        {
            await SendAsync(WorkerProtocol.Error, WorkerPayload.ProtocolError(message));
        }
        catch (WorkerProtocolException)
        {
            // Peer is gone; the reader loop ends on the next read.
        }
    }

    private async ValueTask WriteLineAsync(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _writeGate.WaitAsync();
        try
        {
            await _output.WriteAsync(bytes);
            await _output.FlushAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void ThrowIfGone()
    {
        if (!IsConnected)
        {
            throw new WorkerDisconnectedException($"the worker channel '{_name}' is not connected");
        }
    }

    private sealed class PendingRequest(string id, string responseType, string channelName)
    {
        private readonly TaskCompletionSource<JsonNode?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<JsonNode?> Completion => _completion.Task;

        /// <summary>
        /// Consumes the envelope when it resolves or violates the pending
        /// request: a matching response resolves it, a wrong *response* type
        /// is a protocol violation. Async messages that share the id space
        /// (progress, cancel) are NOT consumed — the caller delivers them to
        /// the message handler.
        /// </summary>
        public bool ResolveOrReject(WorkerEnvelope envelope)
        {
            if (envelope.Type == responseType)
            {
                _completion.TrySetResult(envelope.Payload);
                return true;
            }

            if (WorkerProtocol.ResponseTypes.Contains(envelope.Type))
            {
                _completion.TrySetException(new WorkerProtocolException(
                    $"request {id} on '{channelName}' expected a '{responseType}' response but received '{envelope.Type}'"));
                return true;
            }

            return false;
        }

        public void Fail(WorkerDisconnectedException exception) => _completion.TrySetException(exception);
    }
}
