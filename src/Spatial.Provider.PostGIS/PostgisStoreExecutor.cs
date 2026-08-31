using Spatial.Core.Features;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.PluginSdk.Resources;
using Spatial.Provider.PostGIS.Configuration;
using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Data;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// The store-bound surface of the PostGIS provider (ADR-0028): the facade the
/// guarded capability handlers delegate to after
/// <see cref="PostgisInvocationValidator"/> accepted the invocation. It is
/// wired by <see cref="PostgisStoreServices"/> and routes each capability
/// family to a small focused service — catalogue, dataset, scan, query, batch
/// writes and the transaction lifecycle — and hosts the guard-to-store
/// failure mapping (<see cref="MapFailure"/>) the handlers share. The whole
/// DB path is exercised by the containerised integration suite; the pure SQL,
/// mapping and validation logic above it is unit-tested without a store.
/// </summary>
internal sealed class PostgisStoreExecutor
{
    private readonly PostgisConnectionConfiguration _configuration;
    private readonly PostgisCatalogueService _catalogue;
    private readonly PostgisDatasetService _dataset;
    private readonly PostgisScanService _scan;
    private readonly PostgisQueryService _query;
    private readonly PostgisBatchWriter _writer;
    private readonly PostgisTransactionService _transactions;

    public PostgisStoreExecutor(PostgisConnectionConfiguration configuration, Lazy<PostgisDataStore> store)
    {
        _configuration = configuration;
        (_catalogue, _dataset, _scan, _query, _writer, _transactions) =
            PostgisStoreServices.Create(configuration, store);
    }

    public ValueTask<CapabilityResult> StartCatalogueStreamAsync(
        CapabilityInvocation invocation,
        ICapabilityFacilities facilities,
        string? pattern) =>
        _catalogue.StartCatalogueStreamAsync(invocation, facilities, pattern);

    public ValueTask<CapabilityResult> ExecuteDescribeAsync(
        CapabilityInvocation invocation,
        PostgisDatasetName dataset,
        ICapabilityFacilities facilities) =>
        _dataset.ExecuteDescribeAsync(invocation, dataset, facilities);

    public ValueTask<CapabilityResult> ExecuteCreateAsync(
        CapabilityInvocation invocation,
        PostgisDatasetName dataset,
        FeatureBatch batch,
        int srid) =>
        _dataset.ExecuteCreateAsync(invocation, dataset, batch, srid);

    public ValueTask<CapabilityResult> ExecuteScanAsync(
        CapabilityInvocation invocation,
        PostgisDatasetName dataset,
        ICapabilityFacilities facilities) =>
        _scan.ExecuteScanAsync(invocation, dataset, facilities);

    public ValueTask<CapabilityResult> ExecuteQueryAsync(
        CapabilityInvocation invocation,
        PostgisDatasetName dataset,
        BoundingBox? boundingBox,
        FilterExpression? filter,
        ICapabilityFacilities facilities) =>
        _query.ExecuteQueryAsync(invocation, dataset, boundingBox, filter, facilities);

    public ValueTask<CapabilityResult> ExecuteWriteAsync(
        CapabilityInvocation invocation,
        PostgisDatasetName dataset,
        FeatureBatch batch,
        ResourceId? transactionId) =>
        _writer.ExecuteWriteAsync(invocation, dataset, batch, transactionId);

    public ValueTask<CapabilityResult> ExecuteBeginAsync(CapabilityInvocation invocation, ICapabilityFacilities facilities) =>
        _transactions.ExecuteBeginAsync(invocation, facilities);

    public ValueTask<CapabilityResult> ExecuteEndTransactionAsync(CapabilityInvocation invocation, ResourceId handle, bool commit) =>
        _transactions.ExecuteEndTransactionAsync(invocation, handle, commit);

    /// <summary>Maps a store-side exception to a redacted capability error (the handlers' shared guard).</summary>
    internal CapabilityError MapFailure(CapabilityInvocation invocation, Exception exception) =>
        PostgisFailureMapper.Map(invocation, exception, _configuration);
}
