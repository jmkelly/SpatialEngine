using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
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
            while (true)
            {
                var line = await _lines.ReadLineAsync(_input, MaxLineBytes, cancellationToken);
                if (line is null)
                {
                    return; // clean peer EOF
                }

                if (line.Length == 0)
                {
                    continue;
                }

                WorkerEnvelope envelope;
                try
                {
                    envelope = WorkerWireCodec.Decode(line);
                }
                catch (WorkerProtocolException exception)
                {
                    await TrySendErrorAsync(exception.Message);
                    continue;
                }

                if (envelope.Id is { } id
                    && envelope.IsProtocolV1
                    && _pending.TryGetValue(id, out var pending))
                {
                    pending.Resolve(envelope);
                    continue;
                }

                await _onMessage(envelope);
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

        public void Resolve(WorkerEnvelope envelope)
        {
            if (envelope.Type != responseType)
            {
                _completion.TrySetException(new WorkerProtocolException(
                    $"request {id} on '{channelName}' expected a '{responseType}' response but received '{envelope.Type}'"));
                return;
            }

            _completion.TrySetResult(envelope.Payload);
        }

        public void Fail(WorkerDisconnectedException exception) => _completion.TrySetException(exception);
    }

    /// <summary>
    /// A bounded, chunked line reader for the protocol stream: reads the next
    /// <c>\n</c>-terminated UTF-8 line, carrying partial data between calls so
    /// lines split across OS reads still arrive whole, and refuses lines above
    /// <see cref="MaxLineBytes"/> (a host resource limit, plan §19).
    /// </summary>
    private sealed class LineReader
    {
        private readonly byte[] _chunk = new byte[8192];
        private readonly List<byte> _carry = new(256);
        private int _consumed;

        public async ValueTask<string?> ReadLineAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
        {
            while (true)
            {
                var newline = FindNewline();
                if (newline >= 0)
                {
                    return TakeLine(newline);
                }

                if (_consumed == 0)
                {
                    _consumed = await stream.ReadAsync(_chunk.AsMemory(), cancellationToken);
                    if (_consumed == 0)
                    {
                        return _carry.Count == 0 ? null : TakeDanglingLine();
                    }
                }

                _carry.AddRange(_chunk.AsSpan(0, _consumed));
                _consumed = 0;
                if (_carry.Count > maxBytes)
                {
                    throw new WorkerProtocolException(
                        $"a worker line exceeded the {maxBytes}-byte limit; the peer violated the protocol");
                }
            }
        }

        private int FindNewline()
        {
            for (var i = 0; i < _carry.Count; i++)
            {
                if (_carry[i] == (byte)'\n')
                {
                    return i;
                }
            }

            return -1;
        }

        private string TakeLine(int newline)
        {
            var line = PickLineSansCr(newline);
            _carry.RemoveRange(0, newline + 1);
            return line;
        }

        private string TakeDanglingLine()
        {
            var line = PickLineSansCr(_carry.Count);
            _carry.Clear();
            return line;
        }

        private string PickLineSansCr(int count)
        {
            var span = CollectionsMarshal.AsSpan(_carry)[..count];
            var end = span.Length > 0 && span[^1] == (byte)'\r' ? span.Length - 1 : span.Length;
            return Encoding.UTF8.GetString(span[..end]);
        }
    }
}
