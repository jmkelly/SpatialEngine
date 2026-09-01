namespace Spatial.PluginHost.DotNet.Supervision;

/// <summary>
/// Configuration for one <see cref="WorkerSupervisor"/>: the worker host
/// executable to spawn, the health-check policy, the restart policy and the
/// startup/drain timeouts. All values have defaults; only the executable
/// usually needs attention (most hosts deploy the worker host beside them).
/// </summary>
public sealed class WorkerSupervisorOptions
{
    public WorkerSupervisorOptions(
        string? workerExecutable = null,
        WorkerHealthOptions? health = null,
        RestartPolicy? restartPolicy = null,
        TimeSpan? startupTimeout = null,
        TimeSpan? drainTimeout = null,
        IReadOnlyDictionary<string, string>? workerEnvironment = null)
    {
        WorkerExecutable = workerExecutable ?? WorkerSupervisor.DefaultExecutable;
        Health = health ?? WorkerHealthOptions.Default;
        RestartPolicy = restartPolicy ?? RestartPolicy.Default;
        StartupTimeout = startupTimeout ?? TimeSpan.FromSeconds(10);
        DrainTimeout = drainTimeout ?? TimeSpan.FromSeconds(30);
        WorkerEnvironment = workerEnvironment ?? new Dictionary<string, string>();
    }

    /// <summary>The worker host apphost to spawn (defaults to the one next to this assembly).</summary>
    public string WorkerExecutable { get; }

    public WorkerHealthOptions Health { get; }

    public RestartPolicy RestartPolicy { get; }

    public TimeSpan StartupTimeout { get; }

    public TimeSpan DrainTimeout { get; }

    /// <summary>
    /// The launch environment handed to every spawned worker process
    /// (host-managed provider secrets, ADR-0028/architecture/distilled/host-and-clients.md): providers
    /// read connection configuration from these variables at startup — never
    /// from invocations or the web client. Empty by default.
    /// </summary>
    public IReadOnlyDictionary<string, string> WorkerEnvironment { get; }
}
