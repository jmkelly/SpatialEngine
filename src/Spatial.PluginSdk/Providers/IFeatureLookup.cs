using Spatial.Core.Features;

namespace Spatial.PluginSdk.Providers;

/// <summary>
/// Optional read-by-identity capability (ADR-0038), the read sibling of
/// <see cref="IFeatureEditStore"/> and an additive face alongside
/// <see cref="IFeatureStore"/>. A store that can fetch features by their
/// <see cref="Feature.Id"/> without scanning the whole dataset implements it,
/// so a read-modify-write path (a GeoServices partial update or a per-object
/// delete) resolves its targets with one targeted statement instead of a
/// full-dataset scan. Read-only stores simply omit it; callers fall back to
/// <see cref="IFeatureStore.ScanAsync"/>. Core-typed only, so identity never
/// crosses as a provider or protocol concept.
/// </summary>
public interface IFeatureLookup
{
    /// <summary>
    /// Returns the features whose <see cref="Feature.Id"/> is present in
    /// <paramref name="ids"/>. Ids with no matching feature are absent from
    /// the result (a miss is not an error); the order is unspecified and
    /// duplicates in the input do not duplicate the output.
    /// </summary>
    Task<IReadOnlyList<Feature>> GetAsync(
        string dataset, IReadOnlyList<FeatureId> ids, CancellationToken cancellationToken = default);
}
