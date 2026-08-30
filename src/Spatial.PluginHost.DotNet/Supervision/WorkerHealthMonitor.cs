using System.Collections.Concurrent;
using Spatial.PluginHost.DotNet.Protocol;

namespace Spatial.PluginHost.DotNet.Supervision;

/// <summary>
/// The liveness machinery of the supervision (plan §10.4 "health and
/// compatibility checks"): probes every serving worker on a fixed ping
/// interval, counts consecutive probe failures per worker and escalates to
/// the supervisor's restart path once the threshold trips. Owning the probe
/// loop and the failure bookkeeping keeps the supervisor an orchestrator.
/// </summary>
internal sealed class WorkerHealthMonitor
{
    private readonly WorkerHealthOptions _options;
    private readonly Func<WorkerInstance, bool> _canCheck;
    private readonly Func<WorkerInstance, Task<bool>> _probe;
    private readonly Func<WorkerInstance, int, Task> _restartUnhealthy;
    private readonly ConcurrentDictionary<Guid, int> _failures = new();

    internal WorkerHealthMonitor(
        WorkerHealthOptions options,
        Func<WorkerInstance, bool> canCheck,
        Func<WorkerInstance, Task<bool>> probe,
        Func<WorkerInstance, int, Task> restartUnhealthy)
    {
        _options = options;
        _canCheck = canCheck;
        _probe = probe;
        _restartUnhealthy = restartUnhealthy;
    }

    /// <summary>Probes one worker; repeated failures trip the threshold and escalate to a restart.</summary>
    internal async Task<bool> CheckAsync(WorkerInstance instance)
    {
        if (!_canCheck(instance))
        {
            return false;
        }

        if (!await _probe(instance))
        {
            return await RecordFailureAsync(instance);
        }

        _failures.TryRemove(instance.Id, out _);
        instance.Transition(WorkerState.Healthy, "health check passed");
        return true;
    }

    /// <summary>Counts a probe failure and escalates to a restart once the threshold trips.</summary>
    private async Task<bool> RecordFailureAsync(WorkerInstance instance)
    {
        var failures = _failures.AddOrUpdate(instance.Id, 1, static (_, count) => count + 1);
        if (failures >= _options.FailureThreshold)
        {
            await _restartUnhealthy(instance, failures);
        }

        return false;
    }

    /// <summary>Runs periodic health checks for every serving worker until cancellation.</summary>
    internal async Task RunAsync(Func<IReadOnlyList<WorkerInstance>> servingWorkers, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var instance in servingWorkers())
            {
                await CheckAsync(instance);
            }

            await WaitForPingIntervalAsync(cancellationToken);
        }
    }

    /// <summary>Waits for the next ping interval; cancellation ends the delay cleanly.</summary>
    private async Task WaitForPingIntervalAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.PingIntervalMilliseconds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
