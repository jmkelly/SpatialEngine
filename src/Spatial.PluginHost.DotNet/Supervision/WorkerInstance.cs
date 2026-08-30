using Spatial.PluginHost.DotNet.Protocol;
using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginHost.DotNet.Supervision;

/// <summary>
/// One supervised worker: its package, its lifecycle state and the live
/// process machinery behind it. Internal fields (channel/relay) are swapped
/// when the process restarts; the public surface is the worker's state,
/// timestamps, restart count and last error — the diagnostics the supervisor
/// and hosts surface.
/// </summary>
public sealed class WorkerInstance
{
    private readonly object _gate = new();
    private WorkerState _state = WorkerState.Discovered;

    internal WorkerInstance(PluginPackage package)
    {
        Package = package ?? throw new ArgumentNullException(nameof(package));
        ProviderId = ProviderId.Parse(package.Manifest.Id);
    }

    public Guid Id { get; } = Guid.NewGuid();

    public PluginPackage Package { get; }

    public ProviderId ProviderId { get; }

    public WorkerState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? LastHealthyAt { get; private set; }

    public int RestartCount { get; private set; }

    public string? LastError { get; internal set; }

    public int ProcessId { get; private set; }

    /// <summary>The worker's wire channel; null until the process is up.</summary>
    internal WorkerChannel? Channel { get; set; }

    /// <summary>The worker's process; null until the first spawn.</summary>
    internal WorkerProcess? Process { get; set; }

    /// <summary>Completes with the process's exit code once the process has exited (null while never spawned).</summary>
    public Task<int>? ProcessExit => Process?.WaitForExitAsync();

    /// <summary>The worker's captured stderr diagnostics (empty while never spawned).</summary>
    public string StandardError => Process?.StandardError ?? string.Empty;

    /// <summary>Tracks in-flight invocations and routes progress events.</summary>
    internal WorkerInvocationRelay? Relay { get; set; }

    /// <summary>The provider proxy registered with the capability registry.</summary>
    internal WorkerProvider? Provider { get; set; }

    internal void Transition(WorkerState next, string diagnostic)
    {
        lock (_gate)
        {
            _state = next;
            if (next == WorkerState.Starting || next == WorkerState.Active)
            {
                StartedAt = DateTimeOffset.UtcNow;
            }

            if (next == WorkerState.Healthy || next == WorkerState.Active)
            {
                LastHealthyAt = DateTimeOffset.UtcNow;
            }
        }

        LastError = diagnostic;
    }

    internal void MarkProcess(int processId)
    {
        ProcessId = processId;
    }

    internal void CountRestart()
    {
        RestartCount++;
    }

    internal void Fail(string message)
    {
        LastError = message;
        Transition(WorkerState.Failed, message);
    }

    public override string ToString() => $"{ProviderId} ({State})";
}
