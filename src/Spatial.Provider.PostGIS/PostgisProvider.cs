using Spatial.PluginSdk.Capabilities;
using Spatial.Provider.PostGIS.Configuration;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// The PostGIS data provider (plan §16 Phase 8, Epic G; ADR-0010/0028): a
/// replaceable implementation of the catalogue, dataset, feature and
/// transaction contracts (<c>Spatial.PluginSdk.Providers</c>) on Npgsql 10.
/// The provider is a plugin implementation: it implements
/// <c>Spatial.PluginSdk</c> only and is launched as an isolated worker by
/// the plugin host — the runtime or host never reference it (ADR-0006/0002).
/// Connection secrets are host-managed: the parameterless constructor reads
/// <c>SPATIAL_POSTGIS_CONNECTION</c> from the worker's launch environment
/// (security-model.md); tests inject a configuration directly. Its entire
/// public surface is contracts plus SDK types; every Npgsql and SQL detail
/// lives inside the private adapters (ADR-0005).
/// </summary>
public sealed class PostgisProvider : CapabilityProviderBase
{
    /// <summary>The stable provider identity (<c>postgis@1</c>).</summary>
    public static readonly ProviderId ProviderIdentifier = ProviderId.Parse("postgis@1");

    private readonly PostgisRunner _runner;

    /// <summary>Creates the provider from the worker's launch environment.</summary>
    public PostgisProvider()
        : this(PostgisConnectionConfiguration.FromEnvironment())
    {
    }

    internal PostgisProvider(PostgisConnectionConfiguration configuration)
        : base(PostgisRunner.Descriptors)
    {
        _runner = new PostgisRunner(configuration);
    }

    public override ProviderId Id => ProviderIdentifier;

    public override ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation) => _runner.InvokeAsync(invocation);
}
