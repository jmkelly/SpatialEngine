using Spatial.PluginHost.DotNet.Supervision;
using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Capabilities;

namespace Spatial.Conformance.Tests;

/// <summary>
/// The isolated-worker conformance rig (ADR-0025): a real capability runtime,
/// a supervisor pointing at the worker host apphost, and the packaged
/// NetTopologySuite operations worker. The conformance suite drives exactly
/// the same fixtures through this rig as through the in-process provider —
/// geometry arguments and results travel the wire as canonical binary
/// interchange in the <c>$geometry</c> tag (ADR-0020), and every outcome
/// carries runtime provenance naming the <c>nts@1</c> worker.
/// </summary>
internal sealed class NtsWorkerConformanceRig : IAsyncDisposable
{
    private readonly string _root;
    private readonly string _package;
    private readonly WorkerSupervisor _supervisor;

    private NtsWorkerConformanceRig(string root, string package, WorkerSupervisor supervisor, CapabilityRuntime runtime)
    {
        _root = root;
        _package = package;
        _supervisor = supervisor;
        Runtime = runtime;
    }

    public CapabilityRuntime Runtime { get; }

    public static async Task<NtsWorkerConformanceRig> StartAsync()
    {
        var root = Directory.CreateTempSubdirectory("spatial-nts-").FullName;
        var package = NtsPackageWriter.Write(root);
        var runtime = new CapabilityRuntime(new CapabilityRegistry());
        var supervisor = new WorkerSupervisor(
            runtime,
            new WorkerSupervisorOptions(
                health: new WorkerHealthOptions(
                    PingIntervalMilliseconds: 5000,
                    PingTimeoutMilliseconds: 1000,
                    FailureThreshold: 10),
                startupTimeout: TimeSpan.FromSeconds(15),
                drainTimeout: TimeSpan.FromSeconds(15)));
        var rig = new NtsWorkerConformanceRig(root, package, supervisor, runtime);
        await supervisor.ActivateAsync(package);
        return rig;
    }

    /// <summary>Invokes through the runtime, sending pre-cancelled tokens through too.</summary>
    public Task<CapabilityOutcome> InvokeAsync(
        CapabilityId capability,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var invocation = CapabilityInvocation.Create(capability, arguments) with
        {
            CancellationToken = cancellationToken,
        };
        return Runtime.InvokeAsync(invocation);
    }

    public async ValueTask DisposeAsync()
    {
        await _supervisor.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
