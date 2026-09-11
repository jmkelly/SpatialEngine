using Spatial.Core.Features;

namespace Spatial.PluginSdk.Providers;

/// <summary>
/// The outcome of one feature edit (ADR-0037). Core-typed, so the server
/// facade can attribute a per-feature Esri <c>{objectId, success, error}</c>
/// result without the store ever seeing an Esri concept. <see cref="Id"/> is
/// the feature's identity after the edit — the assigned identity for an add,
/// the existing identity for an update or delete.
/// </summary>
public sealed record FeatureEditOutcome(FeatureId Id, bool Succeeded, string? ErrorCode = null, string? ErrorMessage = null)
{
    /// <summary>A successful edit. </summary>
    public static FeatureEditOutcome Success(FeatureId id) => new(id, true);

    /// <summary>A failed edit carrying the engine error code and message.</summary>
    public static FeatureEditOutcome Failure(FeatureId id, string code, string message) => new(id, false, code, message);
}

/// <summary>
/// Feature add/update/delete over a writable store (ADR-0037), the editing
/// sibling of <see cref="IFeatureStore"/>. Kept as a separate, additive
/// capability so read-only stores (demo, ArcGIS REST) simply do not
/// implement it and the facade advertises read-only capabilities. Each
/// method returns one <see cref="FeatureEditOutcome"/> per input, in input
/// order, so a partially successful batch is reported per feature rather
/// than as one failure. An optional store-owned transaction handle (from
/// <see cref="ITransactionStore"/>) makes a batch atomic when the facade is
/// asked for <c>rollbackOnFailure</c>.
/// </summary>
public interface IFeatureEditStore
{
    /// <summary>Appends a batch; the outcome carries the store-assigned identity.</summary>
    Task<IReadOnlyList<FeatureEditOutcome>> AddAsync(
        string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default);

    /// <summary>Replaces existing features, matched by <see cref="Feature.Id"/>.</summary>
    Task<IReadOnlyList<FeatureEditOutcome>> UpdateAsync(
        string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes existing features by identity.</summary>
    Task<IReadOnlyList<FeatureEditOutcome>> DeleteAsync(
        string dataset, IReadOnlyList<FeatureId> featureIds, string? transaction = null, CancellationToken cancellationToken = default);
}
