using Npgsql;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.PluginSdk.Streams;
using Spatial.Provider.PostGIS.Configuration;
using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Data;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// The store-bound half of the feature stream surface: resolves the scan or
/// query SQL for a dataset, opens the connection and reader, emits canonical
/// feature batches via the pure <see cref="PostgisFeatureBatchEmitter"/> and
/// completes (or fails) the channel with a redacted stream error.
/// </summary>
internal sealed class PostgisFeatureScan
{
    private readonly PostgisConnectionConfiguration _configuration;
    private readonly Lazy<PostgisDataStore> _store;

    public PostgisFeatureScan(PostgisConnectionConfiguration configuration, Lazy<PostgisDataStore> store)
    {
        _configuration = configuration;
        _store = store;
    }

    /// <summary>Streams every row of one dataset as canonical feature batches.</summary>
    public Task EmitScanAsync(
        CapabilityInvocation invocation,
        StreamChannel channel,
        PostgisDatasetName dataset,
        DatasetDescription description,
        CancellationToken token) =>
        EmitAsync(invocation, channel, description, PostgisQueries.Select(dataset, description.Schema), null, token);

    /// <summary>Streams one filtered query, carrying the already-resolved predicate.</summary>
    public Task EmitQueryAsync(
        CapabilityInvocation invocation,
        StreamChannel channel,
        PostgisDatasetName dataset,
        DatasetDescription description,
        PredicateBuild predicate,
        CancellationToken token) =>
        EmitAsync(invocation, channel, description, PostgisQueries.Query(dataset, description.Schema, predicate.Sql), predicate.Parameters, token);

    private async Task EmitAsync(
        CapabilityInvocation invocation,
        StreamChannel channel,
        DatasetDescription description,
        string sql,
        IReadOnlyList<object?>? parameters,
        CancellationToken token)
    {
        try
        {
            await using var connection = await _store.Value.OpenConnectionAsync(token);
            await using var reader = await PostgisDataStore.ExecuteReaderAsync(connection, sql, parameters ?? [], token);
            await PostgisFeatureBatchEmitter.EmitAsync(reader, description.Schema, PostgisSchemaReader.IdentityIndexes(description), channel, token);
            channel.Writer.Complete();
        }
        catch (Exception exception)
        {
            channel.Writer.Complete(PostgisDiagnostics.StreamFailure(invocation.Capability, exception, _configuration));
        }
    }
}
