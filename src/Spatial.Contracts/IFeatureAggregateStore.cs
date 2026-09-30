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
///
/// <para>
/// A store that implements this face honours the whole of the reduction,
/// including the parts that shape the <em>group set</em>: the
/// <see cref="AggregateQuery.Having"/> clause and its
/// <see cref="AggregateQuery.Limit"/>/<see cref="AggregateQuery.Offset"/> page
/// (ADR-0128). There is no second face and no flag to set — the interface is
/// the capability, so a store that answers a reduction owes the clause and the
/// page the same way <see cref="IFeatureStore.QueryAsync"/> owes the plan's cap,
/// and <see cref="AggregatePage.HasMore"/> is how it says there is more, in the
/// same role <see cref="FeatureQueryPage.HasMore"/> plays for a row page.
/// Honouring them is not a pushdown requirement: a store that reduces in
/// managed code has the same obligation as one that writes a <c>GROUP BY</c>.
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
    /// <remarks>
    /// The <see cref="AggregateQuery.Having"/> clause is applied to the reduced
    /// groups and the query's page is cut from the groups that survive it, so
    /// <see cref="AggregatePage.Groups"/> is a page of groups and
    /// <see cref="AggregatePage.HasMore"/> says whether more remain.
    /// </remarks>
    Task<AggregatePage> AggregateAsync(
        string dataset, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken = default);
}
