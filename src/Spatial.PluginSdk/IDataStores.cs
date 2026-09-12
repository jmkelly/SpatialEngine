using Spatial.Core.Features;
using Spatial.PluginSdk.Providers;

namespace Spatial.PluginSdk;

/// <summary>
/// Axis-aligned bounding box filter (x-first). Null means no spatial filter.
/// </summary>
public sealed record BoundingBox(double MinX, double MinY, double MaxX, double MaxY);

/// <summary>
/// Dataset catalogue over a store (ADR-0033, replaces
/// <c>spatial.catalogue.list@1</c> / <c>spatial.dataset.describe@1</c> /
/// <c>spatial.dataset.create@1</c>).
/// </summary>
public interface IDataCatalogue
{
    Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default);

    Task<DatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default);

    Task<string> CreateAsync(string dataset, FeatureBatch sample, int srid, CancellationToken cancellationToken = default);
}

/// <summary>
/// Feature reads/writes over a store (ADR-0033, replaces
/// <c>spatial.feature.scan@1</c> / <c>query@1</c> / <c>write@1</c>).
/// Reads return canonical <see cref="FeatureBatch"/> lists (in-memory pages,
/// no bounded streams); writes append in one transaction.
/// </summary>
public interface IFeatureStore
{
    Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeatureBatch>> QueryAsync(
        string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default);

    Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Store transactions as string handles owned by the store (ADR-0033,
/// replaces <c>spatial.transaction.*@1</c>).
/// </summary>
public interface ITransactionStore
{
    Task<string> BeginAsync(CancellationToken cancellationToken = default);

    Task<bool> CommitAsync(string transaction, CancellationToken cancellationToken = default);

    Task<bool> RollbackAsync(string transaction, CancellationToken cancellationToken = default);
}

/// <summary>Demo-only long-running job (replaces <c>spatial.demo.sleep@1</c>): cancellable delay with progress.</summary>
public interface IDemoJobs
{
    Task<long> SleepAsync(long milliseconds, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}
