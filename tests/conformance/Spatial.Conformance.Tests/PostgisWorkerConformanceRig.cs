using Spatial.PluginHost.DotNet.Supervision;
using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Capabilities;

namespace Spatial.Conformance.Tests;

/// <summary>
/// The isolated-worker conformance rig for the PostGIS provider (ADR-0006/
/// 0025/0028): a real capability runtime, a supervisor pointing at the worker
/// host apphost, and the packaged PostGIS worker launched with an empty
/// <c>SPATIAL_POSTGIS_CONNECTION</c> (host-managed secrets stay out of the
/// store-free matrix — the worker never sees a connection string, so every
/// valid invocation is a redacted, actionable <c>provider.unavailable</c>).
/// The rig drives exactly the same argument/cancellation fixtures as the
/// in-process provider.
/// </summary>
internal sealed class PostgisWorkerConformanceRig : IAsyncDisposable
{
    private readonly string _root;
    private readonly WorkerSupervisor _supervisor;

    private PostgisWorkerConformanceRig(string root, WorkerSupervisor supervisor, CapabilityRuntime runtime)
    {
        _root = root;
        _supervisor = supervisor;
        Runtime = runtime;
    }

    public CapabilityRuntime Runtime { get; }

    public static async Task<PostgisWorkerConformanceRig> StartAsync()
    {
        var root = Directory.CreateTempSubdirectory("spatial-postgis-").FullName;
        var package = PostgisPackageWriter.Write(root);
        var runtime = new CapabilityRuntime(new CapabilityRegistry());
        var supervisor = new WorkerSupervisor(
            runtime,
            new WorkerSupervisorOptions(
                health: new WorkerHealthOptions(
                    PingIntervalMilliseconds: 5000,
                    PingTimeoutMilliseconds: 1000,
                    FailureThreshold: 10),
                startupTimeout: TimeSpan.FromSeconds(20),
                drainTimeout: TimeSpan.FromSeconds(15),
                workerEnvironment: new Dictionary<string, string>
                {
                    // Empty on purpose: the worker must treat a blank
                    // connection configuration as unconfigured (ADR-0028).
                    [Spatial.Provider.PostGIS.Configuration.PostgisConnectionConfiguration.EnvironmentVariable] = string.Empty,
                }));
        var rig = new PostgisWorkerConformanceRig(root, supervisor, runtime);
        await supervisor.ActivateAsync(package);
        return rig;
    }

    public Task<CapabilityOutcome> InvokeAsync(
        CapabilityId capability,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var invocation = CapabilityInvocation.Create(capability, arguments) with
        {
            CancellationToken = cancellationToken,
            GrantedPermissions = DataProviderPermissions,
        };
        return Runtime.InvokeAsync(invocation);
    }

    /// <summary>The permission set the data contracts require (granted by the runtime to reach validation).</summary>
    private static readonly System.Collections.Immutable.ImmutableHashSet<Permission> DataProviderPermissions =
        System.Collections.Immutable.ImmutableHashSet.Create(
            Permission.Parse("spatial.feature.read"),
            Permission.Parse("spatial.feature.write"),
            Permission.Parse("spatial.dataset.create"));

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
