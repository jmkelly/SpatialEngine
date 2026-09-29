namespace Spatial.Core.Features.Query;

/// <summary>
/// One page of a feature read: the batches the plan selected, whether the plan
/// has more pages, the cursor that continues it, and the total the plan matches
/// (ADR-0074 §5, ADR-0116 §2).
///
/// <para>
/// <see cref="HasMore"/> is the explicit "one more" signal: a caller pages
/// until it is false and never has to infer exhaustion from the shape of a
/// token, and a store that knows from the read itself (it fetched the cap and
/// one row past it) can say so without a count. A page that says more remains
/// always carries the <see cref="NextCursor"/> that reaches it, and a page that
/// does not carries none — so a store that can neither count nor over-fetch
/// reports the last page as exactly that.
/// </para>
///
/// <para>
/// <see cref="TotalCount"/> is nullable and <c>null</c> means <em>not
/// computed</em> — never zero. It is the cheap extra a provider may return and
/// the caller may compute instead, which is exactly what makes
/// <c>returnCountOnly</c> an optimisation of a read rather than a capability
/// a store must have.
/// </para>
/// </summary>
/// <param name="Batches">The selected features, in plan order — one page, never the whole match set.</param>
/// <param name="NextCursor">
/// The opaque continuation token, or <c>null</c> when the page is the last one
/// or the plan is not resumable. Only a store issues it; a token a store did
/// not issue for the same plan is <c>invalid.arguments</c> at the store. It is
/// a <em>store position</em>, so a caller that replays it pages the rows where
/// they are rather than a list it no longer holds.
/// </param>
/// <param name="TotalCount">The number of features the plan matches, or <c>null</c> when not computed.</param>
/// <param name="HasMore">Whether the plan has rows beyond this page.</param>
public sealed record FeatureQueryPage(
    IReadOnlyList<FeatureBatch> Batches,
    string? NextCursor = null,
    int? TotalCount = null,
    bool HasMore = false)
{
    /// <summary>A page with no batches, no continuation and no computed total.</summary>
    public static FeatureQueryPage Empty { get; } = new([]);

    /// <summary>The features across every batch, in order.</summary>
    public IEnumerable<Feature> Features => Batches.SelectMany(batch => batch.Features);

    /// <summary>
    /// The page a store read ends on: the features, the continuation when the
    /// plan has more, and the total when it was computed. The one place the
    /// invariant above is written down, so no store has to re-state it.
    /// </summary>
    public static FeatureQueryPage Page(
        IReadOnlyList<FeatureBatch> batches, bool hasMore, string? nextCursor = null, int? totalCount = null) =>
        hasMore
            ? new FeatureQueryPage(batches, nextCursor ?? throw new ArgumentNullException(nameof(nextCursor)), totalCount, true)
            : new FeatureQueryPage(batches, null, totalCount, false);
}
