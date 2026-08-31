using Spatial.PluginHost.DotNet.Manifest;
using Spatial.PluginHost.DotNet.Supervision;
using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Capabilities;

namespace Spatial.Host;

/// <summary>
/// The host's composition root (plan §16 Phase 9, ADR-0018/0030): owns the
/// capability registry, the capability runtime and — when a packages root is
/// configured — the process supervisor that activates immutable plugin
/// packages as isolated workers. The host references no plugin
/// implementation (architecture guard); every provider arrives as a package
/// under <c>Spatial:PackagesRoot</c> and is activated here. The same
/// instance serves HTTP handlers, health and the <c>/api/plugins</c>
/// surface, so the API and the runtime always observe one state.
/// </summary>
public sealed class SpatialHostRuntime : IAsyncDisposable
{
    private readonly WorkerSupervisor? _supervisor;
    private int _disposed;

    private SpatialHostRuntime(CapabilityRuntime runtime, WorkerSupervisor? supervisor)
    {
        Runtime = runtime;
        _supervisor = supervisor;
    }

    /// <summary>The capability runtime all HTTP handlers route through.</summary>
    public CapabilityRuntime Runtime { get; }

    /// <summary>The plugin process supervisor; null when no packages root is configured.</summary>
    public WorkerSupervisor? Supervisor => _supervisor;

    /// <summary>The activated plugin packages (empty when none are configured).</summary>
    public IReadOnlyList<PluginPackage> Packages { get; private set; } = [];

    /// <summary>
    /// Wraps an externally composed capability runtime (for example a test
    /// fixture or an in-process plugin host) with no process supervisor. The
    /// composition seam for hosts that wire providers themselves instead of
    /// activating packages.
    /// </summary>
    public static SpatialHostRuntime For(CapabilityRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return new SpatialHostRuntime(runtime, null);
    }

    /// <summary>
    /// Builds the wired runtime: a fresh capability registry and runtime, a
    /// supervisor with the configured worker environment, and every package
    /// under <c>Spatial:PackagesRoot</c> (when set) discovered and activated.
    /// A package that fails to activate is reported on the supervisor's
    /// events so <c>/api/plugins</c> shows an honest <c>failed</c> state —
    /// one bad package never blocks the rest of the host.
    /// </summary>
    public static async Task<SpatialHostRuntime> CreateAsync(
        IConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        var registry = new CapabilityRegistry();
        var runtime = new CapabilityRuntime(registry, ReadCapabilityConfiguration(configuration));
        var packagesRoot = configuration["Spatial:PackagesRoot"];

        if (string.IsNullOrWhiteSpace(packagesRoot))
        {
            return new SpatialHostRuntime(runtime, null);
        }

        var options = new WorkerSupervisorOptions(
            workerEnvironment: ReadWorkerEnvironment(configuration));
        var supervisor = new WorkerSupervisor(runtime, options);
        var host = new SpatialHostRuntime(runtime, supervisor);
        var logger = loggerFactory.CreateLogger<SpatialHostRuntime>();

        foreach (var package in supervisor.Discover(packagesRoot))
        {
            try
            {
                await supervisor.ActivateAsync(package.Path);
                HostLog.PackageActivated(logger, package.Manifest.Id, package.Path);
            }
            catch (Exception exception)
            {
                HostLog.PackageActivationFailed(logger, exception, package.Manifest.Id, package.Path, exception.Message);
            }
        }

        host.Packages = supervisor.Workers
            .Select(worker => worker.Package)
            .ToArray();
        return host;
    }

    /// <summary>Cleans up supervised workers (drain then stop) — idempotent.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_supervisor is not null)
        {
            await _supervisor.DisposeAsync();
        }
    }

    /// <summary>
    /// Reads the configured capability preferences (<c>Spatial:Preferences</c>,
    /// e.g. <c>"spatial.geometry.buffer@1": "nts@1"</c>) — the immutable
    /// start-up routing (plan §9 resolution step 3). Invalid entries are
    /// skipped; the host logs them nowhere (they are visible in config).
    /// </summary>
    private static CapabilityConfiguration ReadCapabilityConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Spatial:Preferences");
        var preferences = new Dictionary<CapabilityId, ProviderId>();
        foreach (var child in section.GetChildren())
        {
            if (CapabilityId.TryParse(child.Key, out var capability)
                && child.Value is { } providerText
                && ProviderId.TryParse(providerText, out var provider))
            {
                preferences[capability] = provider;
            }
        }

        return CapabilityConfiguration.FromPreferences(preferences);
    }

    /// <summary>
    /// Reads the worker launch environment (<c>Spatial:WorkerEnvironment</c>):
    /// host-managed provider secrets reach workers through their process
    /// environment (ADR-0028, security-model.md) — never through invocations
    /// or the web client.
    /// </summary>
    private static Dictionary<string, string> ReadWorkerEnvironment(IConfiguration configuration)
    {
        var section = configuration.GetSection("Spatial:WorkerEnvironment");
        var environment = new Dictionary<string, string>();
        foreach (var child in section.GetChildren())
        {
            if (child.Value is { } value)
            {
                environment[child.Key] = value;
            }
        }

        return environment;
    }
}
