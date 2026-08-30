using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginHost.DotNet.Supervision;

/// <summary>
/// Per-worker invocation tracking: which invocations are in flight (for drain
/// waits) and where their progress observations should be relayed. Results
/// themselves travel through the worker channel's request/response
/// correlation; this relay carries the async side — progress and the in-flight
/// count that draining waits on.
/// </summary>
public sealed class WorkerInvocationRelay
{
    private readonly ConcurrentDictionary<string, IProgress<ProgressReport>> _progress = new();
    private int _inFlight;

    /// <summary>Registers a started invocation and its progress sink.</summary>
    public void Begin(string invokeId, IProgress<ProgressReport>? progress)
    {
        Interlocked.Increment(ref _inFlight);
        if (progress is not null)
        {
            _progress[invokeId] = progress;
        }
    }

    /// <summary>Forwards a progress observation to the invocation's sink, dropping it when unknown (late/ignored).</summary>
    public void ReportProgress(string invokeId, ProgressReport report)
    {
        if (_progress.TryGetValue(invokeId, out var sink))
        {
            sink.Report(report);
        }
    }

    /// <summary>
    /// Parses a wire <c>progress</c> payload (fraction, message or a bare milestone) and forwards it
    /// as a <see cref="ProgressReport"/>; unknown or malformed invocations are dropped. Keeps the
    /// protocol read-loop handler a one-liner.
    /// </summary>
    public ValueTask TryRelayProgress(string? invokeId, JsonNode? payload)
    {
        if (invokeId is not null && payload is JsonObject obj)
        {
            ReportProgress(invokeId, ReadProgress(obj));
        }

        return ValueTask.CompletedTask;
    }

    private static ProgressReport ReadProgress(JsonObject obj)
    {
        if (obj["fraction"] is JsonValue fraction)
        {
            return ProgressReport.Create(fraction.GetValue<double>(), obj["message"]?.GetValue<string>());
        }

        return ProgressReport.Milestone(obj["message"]?.GetValue<string>() ?? string.Empty);
    }

    /// <summary>Ends an in-flight invocation (result arrived or the caller gave up).</summary>
    public void End(string invokeId)
    {
        _progress.TryRemove(invokeId, out _);
        Interlocked.Decrement(ref _inFlight);
    }

    /// <summary>How many invocations are currently awaiting a result from the worker.</summary>
    public int InFlightCount => Volatile.Read(ref _inFlight);
}
