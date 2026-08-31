using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Data;
using Spatial.Provider.PostGIS.Streams;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// The feature.scan surface (ADR-0028): describes the dataset, mints the
/// feature stream and hands the DB path to <see cref="PostgisFeatureScan"/>.
/// Failures propagate to the guarded runner; stream failures stay on the
/// channel.
/// </summary>
internal sealed class PostgisScanService
{
    private readonly PostgisSchemaReader _schemaReader;
    private readonly PostgisFeatureScan _scan;

    public PostgisScanService(PostgisSchemaReader schemaReader, PostgisFeatureScan scan)
    {
        _schemaReader = schemaReader;
        _scan = scan;
    }

    /// <summary>Streams every row of one dataset as canonical feature batches.</summary>
    public async ValueTask<CapabilityResult> ExecuteScanAsync(
        CapabilityInvocation invocation,
        PostgisDatasetName dataset,
        ICapabilityFacilities facilities)
    {
        var description = await _schemaReader.DescribeAsync(invocation, dataset, invocation.CancellationToken);
        var channel = facilities.Streams.Create(ProviderResourceKinds.FeatureStream, FeatureBatchStream.StreamCapacity);
        _ = _scan.EmitScanAsync(invocation, channel, dataset, description, invocation.CancellationToken);
        return CapabilityResult.Success(channel.Handle);
    }
}
