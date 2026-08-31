using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Data;
using Spatial.Provider.PostGIS.Streams;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// The feature.query surface (ADR-0028): describes the dataset, resolves the
/// bbox + filter predicate, mints the feature stream and hands the DB path
/// to <see cref="PostgisFeatureScan"/>. A rejected filter returns a
/// structured error as the invocation result; failures propagate to the
/// guarded runner; stream failures stay on the channel.
/// </summary>
internal sealed class PostgisQueryService
{
    private readonly PostgisSchemaReader _schemaReader;
    private readonly PostgisFeatureScan _scan;

    public PostgisQueryService(PostgisSchemaReader schemaReader, PostgisFeatureScan scan)
    {
        _schemaReader = schemaReader;
        _scan = scan;
    }

    /// <summary>Runs a filtered query stream, mapping a rejected filter to a structured error.</summary>
    public async ValueTask<CapabilityResult> ExecuteQueryAsync(
        CapabilityInvocation invocation,
        PostgisDatasetName dataset,
        BoundingBox? boundingBox,
        FilterExpression? filter,
        ICapabilityFacilities facilities)
    {
        var description = await _schemaReader.DescribeAsync(invocation, dataset, invocation.CancellationToken);
        var predicate = PostgisInvocationValidator.BuildPredicate(invocation, description, boundingBox, filter);
        if (!predicate.IsValid)
        {
            return CapabilityResult.Failure(predicate.Error!);
        }

        var channel = facilities.Streams.Create(ProviderResourceKinds.FeatureStream, FeatureBatchStream.StreamCapacity);
        _ = _scan.EmitQueryAsync(invocation, channel, dataset, description, predicate, invocation.CancellationToken);
        return CapabilityResult.Success(channel.Handle);
    }
}
