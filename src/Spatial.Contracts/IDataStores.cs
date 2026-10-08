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
/// restriction, the attribute predicate, the bounding-box pre-filter, the
/// projection, the ordering and the paging. Every member is a core value, so
/// no provider or protocol vocabulary crosses the contract; the plan is built
/// and validated once, against the dataset's schema, by
/// <see cref="FeatureQueryValidation"/>, so a bad field name, a negative cap
/// or a clause naming a field the dataset does not have is a typed
/// <c>invalid.arguments</c> at the boundary that received it rather than a
/// per-provider surprise. <see cref="All"/> is the unbounded read, so
/// <c>ScanAsync</c> is this plan by another name.
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
/// <see cref="Where"/> restricts the answer the same way it always did; it
/// arrived as filter <em>text</em> and is parsed once, at the boundary that
/// received it, into this core-typed tree (ADR-0074 §3), so no store parses a
/// grammar and no predicate reaches SQL as text.
/// </para>
///
/// <para>
/// A store appends the feature identity as a final ascending sort key to any
/// <see cref="Order"/>, so the total order is deterministic and the paging
/// above is stable. <see cref="Offset"/> and <see cref="Cursor"/> are
/// alternatives, never composed: a cursor, when present, is the page start.
/// </para>
/// </summary>
/// <param name="Ids">The identities to restrict to, or <c>null</c> for all.</param>
/// <param name="Where">The attribute predicate, or <c>null</c> for all rows.</param>
/// <param name="BoundingBox">The bounding-box pre-filter, or <c>null</c> for all.</param>
/// <param name="Projection">
/// The schema fields to return, in order, or <c>null</c> for every field. A
/// field list is validated against the dataset's schema before the read.
/// </param>
/// <param name="Order">The requested sort keys, or <c>null</c> for the store's own order.</param>
/// <param name="Limit">
/// The maximum number of features to return, or <c>null</c> for no cap — the
/// unbounded read, which is what <see cref="FeatureQuery.All"/> and
/// <see cref="IFeatureStore.ScanAsync"/> are. A caller that wants a page asks
/// for one: the engine's canonical page and batch size is 512 features
/// (<c>FeaturePlanExecutor.BatchSize</c>), and a served surface raises it to
/// its own cap (<c>maxRecordCount</c> on the Esri surface). What the cap buys
/// is stated on the page (<see cref="FeatureQueryPage.HasMore"/>), so a caller
/// never has to fetch a whole match set to learn whether there was more of it.
/// </param>
/// <param name="Offset">The number of features to skip, or <c>null</c> to start at the first.</param>
/// <param name="Cursor">A store-issued continuation token, or <c>null</c> to start the plan.</param>
public sealed record FeatureQuery(
    IReadOnlyList<FeatureId>? Ids = null,
    Predicate? Where = null,
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
        IsUnboundedPlan(Where, Projection, Order, Limit, Offset, Cursor, Ids, BoundingBox);

    private static bool IsUnboundedPlan(
        Predicate? where,
        IReadOnlyList<string>? projection,
        IReadOnlyList<OrderTerm>? order,
        int? limit,
        int? offset,
        string? cursor,
        IReadOnlyList<FeatureId>? ids,
        BoundingBox? boundingBox) =>
        UnrestrictedSelection(where, projection, order, ids, boundingBox)
        && UnpagedPlan(limit, offset, cursor);

    private static bool UnrestrictedSelection(
        Predicate? where,
        IReadOnlyList<string>? projection,
        IReadOnlyList<OrderTerm>? order,
        IReadOnlyList<FeatureId>? ids,
        BoundingBox? boundingBox) =>
        where is null && projection is null && order is null && ids is null && boundingBox is null;

    private static bool UnpagedPlan(int? limit, int? offset, string? cursor) =>
        limit is null && offset is null && cursor is null;
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
/// Writes append in one transaction. The read is a <see cref="FeatureQuery"/>
/// plan rather than a filter string (ADR-0074) and answers a
/// <see cref="FeatureQueryPage"/>, so a row cap is capped in the store rather
/// than after it, and a store pushes what its dialect can express and
/// evaluates the residual over the rows it fetched: the answer is the
/// evaluation of the plan over the whole dataset, in the same order, every
/// time (principle 15).
///
/// <para>
/// <b>Paging is a property of the read, not of the caller</b> (ADR-0116). A
/// store reads the page a plan names: the cap and the page start are the
/// store's, the page says whether more remains, and the continuation is the
/// store's own position, so a large layer answers a small request without the
/// other rows ever being built. A store that has nothing to push the plan into —
/// the in-memory provider answers every plan itself, and a store whose dialect
/// cannot express the plan's order reduces over what it read — still owes its
/// caller the same three things: the <em>answer</em> is one page and never the
/// whole match set, the page states whether more remains, and the continuation
/// is a cursor the store refuses if it did not issue it. What such a store
/// cannot promise is reading fewer rows than it already holds; that is the
/// store's own set, and a caller that needs the rows off the heap needs a store
/// that can push the page down.
/// </para>
/// </summary>
public interface IFeatureStore
{
    Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the features a plan selects, as one page: the batches, whether
    /// more remain, the cursor that continues the plan, and the total when the
    /// store computed it cheaply.
    /// </summary>
    Task<FeatureQueryPage> QueryAsync(
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
