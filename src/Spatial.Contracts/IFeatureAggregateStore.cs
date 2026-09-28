using Spatial.Core.Features.Query;

namespace Spatial.Contracts;

/// <summary>
/// The optional reduction face of a feature store (ADR-0074 §6, the
/// ADR-0033 additive-face pattern): count, distinct and grouped aggregate over
/// the features a plan selects. Everything here <em>reduces</em> — it changes
/// the shape of the answer from features to a number, a row set or group rows
/// — which is why it is additive rather than part of
/// <see cref="IFeatureStore"/>.
///
/// <para>
/// A store that does not implement this face is not less capable, only
/// unhurried: it still answers <see cref="IFeatureStore.QueryAsync"/>
/// correctly, and the caller computes the reduction over the page it gets back
/// (the engine's <c>FeatureReductionFallback</c>). What a store
/// <em>must</em> not do is answer a reduction differently from the reference
/// semantics when it does implement the face: a pushed-down
/// <c>COUNT</c>/<c>GROUP BY</c> and the in-memory reduction are compared
/// value for value by the shared conformance suite, so principle 15's
/// "optional but semantics-preserving" is checkable.
/// </para>
///
/// <para>
/// Every method is a cancellable task; a plan the dataset's schema does not
/// admit is <c>invalid.arguments</c> (see <see cref="FeatureQueryValidation"/>)
/// and a store that cannot be reached is <c>store.unavailable</c>.
/// </para>
/// </summary>
public interface IFeatureAggregateStore
{
    /// <summary>
    /// The number of features the plan selects, counted by the store when it
    /// can. Counted <em>after</em> the plan's spatial and attribute
    /// restrictions, so a count is the size of the set the same plan would
    /// read.
    /// </summary>
    Task<int> CountAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The deduplicated combinations of the requested fields over the features
    /// the plan selects, in first-seen order, with the total when the store
    /// computed it.
    /// </summary>
    Task<DistinctPage> DistinctAsync(
        string dataset, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken = default);

    /// <summary>
    /// The grouped reduction of the features the plan selects, with the group
    /// and result names and the group count when the store computed it. A plan
    /// that groups nothing over an empty set yields exactly one group of null
    /// values — the served statistics surface's "one row of nulls" rule, so
    /// the reduction of an empty set is never an empty answer.
    /// </summary>
    Task<AggregatePage> AggregateAsync(
        string dataset, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken = default);
}
