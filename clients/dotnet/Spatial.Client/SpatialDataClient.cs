using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Codec;

namespace Spatial.Client;

/// <summary>
/// The data surface of the .NET client: the store catalogue and datasets,
/// feature scan/query/write over canonical SFBAT Base64, and the transaction
/// lifecycle. Split from <see cref="SpatialClient"/> so the client's fan-out
/// stays deliberate (ADR-0040); reach it through
/// <see cref="SpatialClient.Data"/>.
/// </summary>
public sealed class SpatialDataClient
{
    private readonly SpatialClientTransport _transport;

    internal SpatialDataClient(SpatialClientTransport transport) => _transport = transport;

    /// <summary>Lists the datasets a store publishes.</summary>
    public async Task<IReadOnlyList<DatasetSummary>> ListCatalogueAsync(
        string store = "demo", string? pattern = null, CancellationToken cancellationToken = default)
    {
        var url = $"/api/catalogue?store={Uri.EscapeDataString(store)}"
            + (pattern is null ? string.Empty : $"&pattern={Uri.EscapeDataString(pattern)}");
        var response = await _transport.GetAsync<CatalogueResponse>(url, cancellationToken);
        return response.Datasets;
    }

    /// <summary>Describes one dataset: its fields, extent and identity column.</summary>
    public Task<DatasetDescription> DescribeDatasetAsync(
        string dataset, string store = "demo", CancellationToken cancellationToken = default) =>
        _transport.GetAsync<DatasetDescription>(
            $"/api/datasets/{Uri.EscapeDataString(dataset)}?store={Uri.EscapeDataString(store)}", cancellationToken);

    /// <summary>Creates a dataset from a sample batch and returns its name.</summary>
    public async Task<string> CreateDatasetAsync(
        string dataset, FeatureBatch sample, int srid, string store = "postgis", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var response = await _transport.PostAsync<CreateDatasetResponse>(
            $"/api/datasets?store={Uri.EscapeDataString(store)}",
            new CreateDatasetRequest(dataset, Convert.ToBase64String(FeatureBatchCodec.Encode(sample)), srid),
            cancellationToken);
        return response.Dataset;
    }

    /// <summary>Scans every feature in a dataset.</summary>
    public async Task<IReadOnlyList<FeatureBatch>> ScanAsync(
        string dataset, string store = "demo", CancellationToken cancellationToken = default)
    {
        var response = await _transport.PostAsync<FeatureBatchesResponse>(
            $"/api/features/scan?store={Uri.EscapeDataString(store)}",
            new ScanRequest(dataset), cancellationToken);
        return response.Batches.Select(SpatialClientCodec.DecodeBatch).ToArray();
    }

    /// <summary>Queries features by extent, filter expression and store.</summary>
    public async Task<IReadOnlyList<FeatureBatch>> QueryAsync(
        string dataset, Contracts.BoundingBox? bbox = null, string? filter = null,
        string store = "demo", CancellationToken cancellationToken = default)
    {
        var response = await _transport.PostAsync<FeatureBatchesResponse>(
            $"/api/features/query?store={Uri.EscapeDataString(store)}",
            new FeatureQueryRequest(dataset, bbox is null ? null : new BboxDto(bbox.MinX, bbox.MinY, bbox.MaxX, bbox.MaxY), filter),
            cancellationToken);
        return response.Batches.Select(SpatialClientCodec.DecodeBatch).ToArray();
    }

    /// <summary>Appends features to a dataset, optionally inside a transaction, and returns the count.</summary>
    public async Task<int> WriteAsync(
        string dataset, FeatureBatch batch, string? transaction = null,
        string store = "postgis", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var response = await _transport.PostAsync<FeatureWriteResponse>(
            $"/api/features/write?store={Uri.EscapeDataString(store)}",
            new FeatureWriteRequest(dataset, Convert.ToBase64String(FeatureBatchCodec.Encode(batch)), transaction),
            cancellationToken);
        return response.Appended;
    }

    /// <summary>Begins a transaction and returns its handle.</summary>
    public async Task<string> BeginTransactionAsync(string store = "postgis", CancellationToken cancellationToken = default)
    {
        var response = await _transport.PostAsync<BeginTransactionResponse>(
            $"/api/transactions/begin?store={Uri.EscapeDataString(store)}", new object(), cancellationToken);
        return response.Transaction;
    }

    /// <summary>Commits a transaction.</summary>
    public async Task<bool> CommitTransactionAsync(string transaction, string store = "postgis", CancellationToken cancellationToken = default)
    {
        var response = await _transport.PostAsync<TransactionResponse>(
            $"/api/transactions/commit?store={Uri.EscapeDataString(store)}",
            new TransactionRequest(transaction), cancellationToken);
        return response.Ok;
    }

    /// <summary>Rolls a transaction back.</summary>
    public async Task<bool> RollbackTransactionAsync(string transaction, string store = "postgis", CancellationToken cancellationToken = default)
    {
        var response = await _transport.PostAsync<TransactionResponse>(
            $"/api/transactions/rollback?store={Uri.EscapeDataString(store)}",
            new TransactionRequest(transaction), cancellationToken);
        return response.Ok;
    }
}
