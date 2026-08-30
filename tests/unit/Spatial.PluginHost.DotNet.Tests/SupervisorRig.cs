using Spatial.PluginHost.DotNet.Supervision;
using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Capabilities;

namespace Spatial.PluginHost.DotNet.Tests;

/// <summary>
/// The supervisor test rig: a real capability runtime, a supervisor pointing
/// at the worker host apphost, and packaged fixture v1/v2 directories in a
/// temp root. Tests drive the full process boundary: activate, invoke,
/// monitor and drain real worker processes.
/// </summary>
internal sealed class SupervisorRig : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("spatial-supervisor-").FullName;

    private SupervisorRig(CapabilityRuntime runtime, WorkerSupervisor supervisor)
    {
        Runtime = runtime;
        Supervisor = supervisor;
        Registry = runtime.Registry;
        V1Package = PackageWriter.Write(_root, FixtureManifest.V1());
        V2Package = PackageWriter.Write(_root, FixtureManifest.V2());
    }

    public CapabilityRegistry Registry { get; }

    public CapabilityRuntime Runtime { get; }

    public WorkerSupervisor Supervisor { get; }

    public string V1Package { get; }

    public string V2Package { get; }

    public static SupervisorRig Create(
        RestartPolicy? restartPolicy = null,
        int healthFailureThreshold = 3,
        TimeSpan? drainTimeout = null)
    {
        var runtime = new CapabilityRuntime(new CapabilityRegistry());
        var supervisor = new WorkerSupervisor(
            runtime,
            new WorkerSupervisorOptions(
                health: new WorkerHealthOptions(
                    PingIntervalMilliseconds: 1000,
                    PingTimeoutMilliseconds: 1000,
                    FailureThreshold: healthFailureThreshold),
                restartPolicy: restartPolicy ?? RestartPolicy.Default,
                startupTimeout: TimeSpan.FromSeconds(10),
                drainTimeout: drainTimeout ?? TimeSpan.FromSeconds(15)));
        return new SupervisorRig(runtime, supervisor);
    }

    /// <summary>Invokes through the runtime (jobs for long-running capabilities, inline otherwise).</summary>
    public async Task<CapabilityOutcome> InvokeAsync(
        CapabilityId capability,
        IReadOnlyDictionary<string, object?>? arguments = null,
        DateTimeOffset? deadline = null)
    {
        var invocation = CapabilityInvocation.Create(capability, arguments ?? new Dictionary<string, object?>());
        if (deadline is { } due)
        {
            invocation = invocation with { Deadline = due };
        }

        return await Runtime.InvokeAsync(invocation);
    }

    public static async Task WaitForAsync(SupervisorRig rig, Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        _ = rig;
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(25);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Supervisor.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
