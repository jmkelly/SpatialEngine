using Spatial.Core.Features;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.PluginSdk.Streams;
using Spatial.Provider.PostGIS.Configuration;
using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Data;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// The dataset.describe and dataset.create surface (ADR-0028): describe emits
/// the single-item description stream through
/// <see cref="PostgisMetadataEmitter"/>; create builds the result table for a
/// validated batch schema and SRID. Failures propagate to the guarded facade.
/// </summary>
internal sealed class PostgisDatasetService
{
    private readonly PostgisConnectionConfiguration _configuration;
    private readonly Lazy<PostgisDataStore> _store;
    private readonly PostgisSchemaReader _schemaReader;

    public PostgisDatasetService(
        PostgisConnectionConfiguration configuration,
        Lazy<PostgisDataStore> store,
        PostgisSchemaReader schemaReader)
    {
        _configuration = configuration;
        _store = store;
        _schemaReader = schemaReader;
    }

    /// <summary>Describes one dataset and emits the single-item description stream.</summary>
    public async ValueTask<CapabilityResult> ExecuteDescribeAsync(
        CapabilityInvocation invocation,
        PostgisDatasetName dataset,
        ICapabilityFacilities facilities)
    {
        var description = await _schemaReader.DescribeAsync(invocation, dataset, invocation.CancellationToken);
        var channel = facilities.Streams.Create(ProviderResourceKinds.DatasetStream, 1);
        _ = PostgisMetadataEmitter.EmitDescriptionAsync(_configuration, invocation, channel, description, invocation.CancellationToken);
        return CapabilityResult.Success(channel.Handle);
    }

    /// <summary>Creates the result table for a validated batch schema and SRID.</summary>
    public async ValueTask<CapabilityResult> ExecuteCreateAsync(
        CapabilityInvocation invocation,
        PostgisDatasetName dataset,
        FeatureBatch batch,
        int srid)
    {
        await using var connection = await _store.Value.OpenConnectionAsync(invocation.CancellationToken);
        await PostgisDataStore.ExecuteNonQueryAsync(
            connection, PostgisQueries.CreateTable(dataset, batch.Schema, srid), [], invocation.CancellationToken);
        return CapabilityResult.Success(dataset.Qualified);
    }
}
