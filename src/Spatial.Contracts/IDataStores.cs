using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.Contracts;

/// <summary>
/// Axis-aligned bounding box filter (x-first). Null means no spatial filter.
/// </summary>
public sealed record BoundingBox(double MinX, double MinY, double MaxX, double MaxY);

/// <summary>
/// The feature-read plan a store answers (ADR-0074 §1): the identity
/// restriction, the attribute predicate and the bounding-box pre-filter.
/// ADR-0074 §5 adds the row cap, cursor and ordering with the paging face;
/// until then a plan selects rows. Every member is a core value, so no
/// provider or protocol vocabulary crosses the contract; the plan is built
/// and validated once, against the dataset's schema, at the boundary that
/// received it. <see cref="All"/> is the unbounded read, so
/// <c>ScanAsync</c> is this plan by another name.
/// </summary>
public sealed record FeatureQuery(
    IReadOnlyList<FeatureId>? Ids = null,
    Predicate? Where = null,
    BoundingBox? BoundingBox = null)
{
    /// <summary>The unbounded plan: every feature of the dataset.</summary>
    public static FeatureQuery All { get; } = new();
}

/// <summary>
/// Dataset catalogue over a store (ADR-0033; the versioned worker contracts are history).
/// </summary>
public interface IDataCatalogue
{
    Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default);

    Task<DatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default);

    Task<string> CreateAsync(string dataset, FeatureBatch sample, int srid, CancellationToken cancellationToken = default);
}

/// <summary>
/// Feature reads/writes over a store (ADR-0033; the versioned worker contracts are history).
/// Reads return canonical <see cref="FeatureBatch"/> lists (in-memory pages,
/// no bounded streams); writes append in one transaction. The read is a
/// <see cref="FeatureQuery"/> plan rather than a filter string (ADR-0074): a
/// store answers it with what its dialect can push down and evaluates the
/// residual over the rows it fetched, so the answer is the evaluation of the
/// plan over the whole dataset, every time.
/// </summary>
public interface IFeatureStore
{
    Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeatureBatch>> QueryAsync(
        string dataset, FeatureQuery query, CancellationToken cancellationToken = default);

    Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Store transactions as string handles owned by the store (ADR-0033;
/// the versioned worker contracts are history).
/// </summary>
public interface ITransactionStore
{
    Task<string> BeginAsync(CancellationToken cancellationToken = default);

    Task<bool> CommitAsync(string transaction, CancellationToken cancellationToken = default);

    Task<bool> RollbackAsync(string transaction, CancellationToken cancellationToken = default);
}

/// <summary>Demo-only cancellable delay with progress (ADR-0033; there is no job model, and the versioned worker contracts are history).</summary>
public interface IDemoWork
{
    Task<long> SleepAsync(long milliseconds, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}
