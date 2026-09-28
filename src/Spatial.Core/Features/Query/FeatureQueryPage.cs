namespace Spatial.Core.Features.Query;

/// <summary>
/// One page of a feature read: the batches the plan selected, the cursor that
/// continues the same plan, and the total the plan matches (ADR-0074 §5).
///
/// <para>
/// <see cref="TotalCount"/> is nullable and <c>null</c> means <em>not
/// computed</em> — never zero. It is the cheap extra a provider may return and
/// the caller may compute instead, which is exactly what makes
/// <c>returnCountOnly</c> an optimisation of a read rather than a capability
/// a store must have.
/// </para>
/// </summary>
/// <param name="Batches">The selected features, in plan order.</param>
/// <param name="NextCursor">
/// The opaque continuation token, or <c>null</c> when the page is the last one
/// or the plan is not resumable. Only a store issues it; a token a store did
/// not issue for the same plan is <c>invalid.arguments</c> at the store.
/// </param>
/// <param name="TotalCount">The number of features the plan matches, or <c>null</c> when not computed.</param>
public sealed record FeatureQueryPage(
    IReadOnlyList<FeatureBatch> Batches,
    string? NextCursor = null,
    int? TotalCount = null)
{
    /// <summary>A page with no batches and no computed total.</summary>
    public static FeatureQueryPage Empty { get; } = new([]);

    /// <summary>The features across every batch, in order.</summary>
    public IEnumerable<Feature> Features => Batches.SelectMany(batch => batch.Features);
}
