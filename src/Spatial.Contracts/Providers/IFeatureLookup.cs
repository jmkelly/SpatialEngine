using Spatial.Core.Features;

namespace Spatial.Contracts.Providers;

/// <summary>
/// Optional read-by-identity face (ADR-0038), the read sibling of
/// <see cref="IFeatureEditStore"/> and an additive face alongside
/// <see cref="IFeatureStore"/>. A store that can fetch features by their
/// <see cref="Feature.Id"/> without scanning the whole dataset implements it,
/// so a read-modify-write path (a GeoServices partial update or a per-object
/// delete) resolves its targets with one targeted statement instead of a
/// full-dataset scan. Read-only stores simply omit it; callers fall back to
/// <see cref="IFeatureStore.ScanAsync"/>. Core-typed only, so identity never
/// crosses as a provider or protocol concept.
///
/// <para>
/// <b>The read is answerable only for a dataset that declares an identity
/// column</b> (ADR-0140). A dataset that declares none has no durable key —
/// its rows are named by the order they were read in, which any write moves —
/// so a store must fail the read with <c>invalid.arguments</c> rather than
/// answer it. Answering is wrong twice over: an empty result claims every
/// requested identity is absent, and a result keyed by a read ordinal is a
/// claim about a different feature after the next write. A miss is not an
/// error; this is. They are different answers and a caller must be able to
/// tell them apart.
/// </para>
/// </summary>
public interface IFeatureLookup
{
    /// <summary>
    /// Returns the features whose <see cref="Feature.Id"/> is present in
    /// <paramref name="ids"/>. Ids with no matching feature are absent from
    /// the result (a miss is not an error); the order is unspecified and
    /// duplicates in the input do not duplicate the output.
    ///
    /// <para>
    /// A dataset that declares no identity column is a typed
    /// <c>invalid.arguments</c> naming the dataset, whatever
    /// <paramref name="ids"/> holds (ADR-0140).
    /// </para>
    /// </summary>
    Task<IReadOnlyList<Feature>> GetAsync(
        string dataset, IReadOnlyList<FeatureId> ids, CancellationToken cancellationToken = default);
}
