using Spatial.PluginHost.DotNet.Protocol;

namespace Spatial.PluginHost.DotNet.Supervision;
/// <summary>
/// Health-check configuration: how often an active worker is pinged, how long
/// a ping may take and how many consecutive failures mark it unhealthy (plan
/// §10.4 "health and compatibility checks").
/// </summary>
public sealed record WorkerHealthOptions(
    int PingIntervalMilliseconds = 5000,
    int PingTimeoutMilliseconds = 2000,
    int FailureThreshold = 3)
{
    public static WorkerHealthOptions Default { get; } = new();
}

/// <summary>
/// Liveness probing over the wire protocol: a <c>ping</c> that must be
/// answered with a <c>pong</c> from the same worker. A worker that fails
/// <see cref="WorkerHealthOptions.FailureThreshold"/> consecutive probes is
/// unhealthy and restarted.
/// </summary>
public static class WorkerHealth
{
    /// <summary>
    /// Probes one worker. Returns true when the worker answers with a pong
    /// within <paramref name="timeout"/>; false for a timeout, a protocol
    /// fault or a disconnect.
    /// </summary>
    public static async Task<bool> ProbeAsync(
        WorkerChannel channel,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await channel.RequestAsync(
                WorkerProtocol.Ping,
                WorkerPayload.Ping(Guid.NewGuid().ToString("N")),
                WorkerProtocol.Pong,
                timeout,
                cancellationToken: cancellationToken);
            return response is not null;
        }
        catch (Exception exception) when (exception is WorkerProtocolException or OperationCanceledException)
        {
            return false;
        }
    }
}
