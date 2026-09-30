using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.Querying;

/// <summary>
/// The fallback reads: the correct answer for a store that cannot push a plan
/// or a reduction down, computed over the whole dataset in memory
/// (ADR-0074 §4, §6). Pushdown is optional; <em>the answer</em> is not, so
/// every read has a path that is right before it is fast.
///
/// <para>
/// Two callers use this. A store implements its plan read by handing the
/// request here, which is how the in-memory providers are correct on day one
/// and how a provider's own residual evaluation shares one definition with the
/// pushdown it complements. A caller whose store does not implement
/// <see cref="IFeatureAggregateStore"/> reduces the page itself, exactly as
/// ADR-0074 §6 requires.
/// </para>
///
/// <para>
/// A plan with a predicate needs an evaluator, which is store-side code (the
/// per-provider in-memory evaluators), so this fallback answers the plan
/// members a store can be asked to honour without one — the identity
/// restriction and the bounding-box pre-filter — plus the projection, the
/// ordering and the paging. A caller that has a predicate to honour asks a
/// store that evaluates it, or evaluates it itself over the rows the read
/// fetched; it never silently drops one.
/// </para>
/// </summary>
public static class FeaturePlanFallback
{
    /// <summary>Reads a plan over the whole dataset, evaluating it in memory.</summary>
    public static async Task<FeatureQueryPage> ReadAsync(
        IFeatureStore store, string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(query);

        var batches = await store.ScanAsync(dataset, cancellationToken).ConfigureAwait(false);
        var schema = Schema(batches, dataset);
        return FeaturePlanExecutor.Execute(schema, Features(batches), query, cancellationToken);
    }

    /// <summary>Counts the features a plan selects, evaluated in memory.</summary>
    public static async Task<int> CountAsync(
        IFeatureStore store, string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
        FeatureReduction.CountFeatures(
            (await SelectAsync(store, dataset, query, cancellationToken).ConfigureAwait(false)).Selected);

    /// <summary>The deduplicated field combinations a plan selects, evaluated in memory.</summary>
    public static async Task<DistinctPage> DistinctAsync(
        IFeatureStore store, string dataset, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken = default)
    {
        var (schema, selected) = await SelectAsync(store, dataset, query, cancellationToken).ConfigureAwait(false);
        return FeatureReduction.Distinct(schema, selected, distinct);
    }

    /// <summary>The grouped reduction a plan selects, evaluated in memory.</summary>
    public static async Task<AggregatePage> AggregateAsync(
        IFeatureStore store, string dataset, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken = default)
    {
        var (schema, selected) = await SelectAsync(store, dataset, query, cancellationToken).ConfigureAwait(false);

        // The plan's order is handed to the reduction, not applied to the rows:
        // a group order is a total order over the *groups*, and only the
        // reduction knows which rows a group has (ADR-0115 §4).
        return FeatureReduction.Aggregate(schema, selected, aggregate, query.Order);
    }

    private static async Task<(FeatureSchema Schema, IReadOnlyList<Feature> Selected)> SelectAsync(
        IFeatureStore store, string dataset, FeatureQuery query, CancellationToken cancellationToken)
    {
        var batches = await store.ScanAsync(dataset, cancellationToken).ConfigureAwait(false);
        var schema = Schema(batches, dataset);
        return (schema, FeaturePlanExecutor.Select(schema, Features(batches), query, cancellationToken));
    }

    private static List<Feature> Features(IReadOnlyList<FeatureBatch> batches) =>
        batches.SelectMany(batch => batch.Features).ToList();

    /// <summary>
    /// The schema of a read: the first batch's, because every store emits at
    /// least one (possibly empty) batch. A read that emitted none is a store
    /// that broke the batch contract, and is reported rather than guessed at.
    /// </summary>
    private static FeatureSchema Schema(IReadOnlyList<FeatureBatch> batches, string dataset) =>
        batches.Count > 0
            ? batches[0].Schema
            : throw SpatialException.BadArguments($"The store returned no batch for dataset '{dataset}'.");
}

/// <summary>
/// The caller-side reduction of a store that does not implement
/// <see cref="IFeatureAggregateStore"/> (ADR-0074 §6). The caller probes the
/// face once — the interface is the capability, so there is no flag to lie —
/// and reduces over what the store answered when it is absent. The answers are
/// the same either way; only the cost differs.
/// </summary>
public static class FeatureReductionFallback
{
    /// <summary>
    /// The count, distinct set or grouped reduction of a plan, computed by the
    /// store when it implements <see cref="IFeatureAggregateStore"/> and over
    /// the whole dataset in memory when it does not. A store's own answer is
    /// preferred whenever it exists, so a pushdown is never bypassed.
    /// </summary>
    public static Task<int> CountAsync(
        IFeatureStore store, string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
        store is IFeatureAggregateStore aggregates
            ? aggregates.CountAsync(dataset, query, cancellationToken)
            : FeaturePlanFallback.CountAsync(store, dataset, query, cancellationToken);

    /// <inheritdoc cref="CountAsync(IFeatureStore, string, FeatureQuery, CancellationToken)"/>
    public static Task<DistinctPage> DistinctAsync(
        IFeatureStore store, string dataset, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken = default) =>
        store is IFeatureAggregateStore aggregates
            ? aggregates.DistinctAsync(dataset, query, distinct, cancellationToken)
            : FeaturePlanFallback.DistinctAsync(store, dataset, query, distinct, cancellationToken);

    /// <inheritdoc cref="CountAsync(IFeatureStore, string, FeatureQuery, CancellationToken)"/>
    public static Task<AggregatePage> AggregateAsync(
        IFeatureStore store, string dataset, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken = default) =>
        store is IFeatureAggregateStore aggregates
            ? aggregates.AggregateAsync(dataset, query, aggregate, cancellationToken)
            : FeaturePlanFallback.AggregateAsync(store, dataset, query, aggregate, cancellationToken);
}
