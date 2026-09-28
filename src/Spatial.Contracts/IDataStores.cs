using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.Contracts;

/// <summary>
/// Axis-aligned bounding box filter (x-first). Null means no spatial filter.
/// </summary>
public sealed record BoundingBox(double MinX, double MinY, double MaxX, double MaxY);

/// <summary>
/// The feature-read plan a store answers (ADR-0074 §1, §5): the identity
/// restriction, the bounding-box pre-filter, the projection, the ordering and
/// the paging. Every member is a core value, so no provider or protocol
/// vocabulary crosses the contract; the plan is built and validated once,
/// against the dataset's schema, by <see cref="FeatureQueryValidation"/>, so a
/// bad field name or a negative cap is a typed <c>invalid.arguments</c> at the
/// boundary that received it rather than a per-provider surprise.
/// <see cref="All"/> is the unbounded read, so <c>ScanAsync</c> is this plan
/// by another name.
///
/// <para>
/// A plan changes the <em>shape</em> of the answer only in the ways a
/// <see cref="IFeatureStore"/> can answer without reducing: fewer attributes
/// (<see cref="Projection"/>), a different sequence (<see cref="Order"/>), or
/// fewer rows (<see cref="Limit"/>, <see cref="Offset"/>, and
/// <see cref="Cursor"/> continuing the same plan). Everything that asks the
/// store to <em>reduce</em> — count, distinct, aggregate — is additive and
/// lives on <see cref="IFeatureAggregateStore"/>, so a store that cannot
/// reduce still answers this read correctly.
/// </para>
///
/// <para>
/// A store appends the feature identity as a final ascending sort key to any
/// <see cref="Order"/>, so the total order is deterministic and the paging
/// above is stable. <see cref="Offset"/> and <see cref="Cursor"/> are
/// alternatives, never composed: a cursor, when present, is the page start.
/// </para>
///
/// <para>
/// <b>Merge note.</b> ADR-0074 §1 also gives the plan its
/// <c>Predicate? Where</c> member, and the predicate vocabulary is landed by
/// SpatialEngine-u2x.8. This branch carries the plan without it, because the
/// predicate grammar is that bead's scope; the two branches' versions of this
/// record are merged as the <em>union</em> of their members, not one side's
/// copy. See the conflict bead filed with this change.
/// </para>
/// </summary>
/// <param name="Ids">The identities to restrict to, or <c>null</c> for all.</param>
/// <param name="BoundingBox">The bounding-box pre-filter, or <c>null</c> for all.</param>
/// <param name="Projection">
/// The schema fields to return, in order, or <c>null</c> for every field. A
/// field list is validated against the dataset's schema before the read.
/// </param>
/// <param name="Order">The requested sort keys, or <c>null</c> for the store's own order.</param>
/// <param name="Limit">The maximum number of features to return, or <c>null</c> for no cap.</param>
/// <param name="Offset">The number of features to skip, or <c>null</c> to start at the first.</param>
/// <param name="Cursor">A store-issued continuation token, or <c>null</c> to start the plan.</param>
public sealed record FeatureQuery(
    IReadOnlyList<FeatureId>? Ids = null,
    BoundingBox? BoundingBox = null,
    IReadOnlyList<string>? Projection = null,
    IReadOnlyList<OrderTerm>? Order = null,
    int? Limit = null,
    int? Offset = null,
    string? Cursor = null)
{
    /// <summary>The unbounded plan: every feature of the dataset.</summary>
    public static FeatureQuery All { get; } = new();

    /// <summary>Whether the plan asks for anything beyond selecting every feature.</summary>
    public bool IsUnbounded =>
        Projection is null && Order is null && Limit is null && Offset is null && Cursor is null
        && Ids is null && BoundingBox is null;
}

public interface IDataCatalogue
{
    Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default);

    Task<DatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default);

    Task<string> CreateAsync(string dataset, FeatureBatch sample, int srid, CancellationToken cancellationToken = default);
}

/// <summary>
/// Feature reads/writes over a store (ADR-0033; the versioned worker contracts are history).
/// Writes append in one transaction. The plan read is a
/// <see cref="FeatureQuery"/> rather than a filter string (ADR-0074), and
/// answers a <see cref="FeatureQueryPage"/>, so a row cap is capped in the
/// store rather than after it, and a store pushes what its dialect can express
/// and evaluates the residual over the rows it fetched: the answer is the
/// evaluation of the plan over the whole dataset, in the same order, every
/// time (principle 15).
/// </summary>
public interface IFeatureStore
{
    Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the features a plan selects, as one page: the batches, the
    /// cursor that continues the plan, and the total when the store computed
    /// it cheaply.
    /// </summary>
    Task<FeatureQueryPage> QueryAsync(
        string dataset, FeatureQuery query, CancellationToken cancellationToken = default);

    Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The published filter <em>text</em> read, superseded by the plan read.
    /// It is what the boundary received before the plan existed
    /// (ADR-0074 §3): the text is parsed once, at the boundary, into the
    /// plan's predicate — which SpatialEngine-u2x.8 lands — and this member
    /// goes with it. It stays until then so the filter text a client sends
    /// keeps working in the meantime.
    /// </summary>
    Task<IReadOnlyList<FeatureBatch>> QueryFilterAsync(
        string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default);
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
