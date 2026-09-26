using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Resolving the feature an attachment resource addresses: the Esri
/// <c>OBJECTID</c> to its feature, exactly as <c>query</c> and the feature
/// resource do, so attachments agree with <c>returnIdsOnly</c>. Shared by
/// every attachment resource, which is why it is its own type rather than a
/// preamble each of them repeats (ADR-0040).
/// </summary>
internal static class FeatureAttachmentTargets
{
    /// <summary>
    /// The feature an attachment resource addresses: the published layer,
    /// the store that holds it, its optional blob face and the Esri
    /// <c>OBJECTID</c>. Grouping them keeps each resource's signature to the
    /// ids it addresses.
    /// </summary>
    internal sealed record AttachmentTarget(
        DatasetDescription Dataset,
        IFeatureStore Store,
        IFeatureAttachmentStore? Attachments,
        long ObjectId);

    /// <summary>The target's feature by its Esri OBJECTID, the one preamble every per-feature resource repeats.</summary>
    internal static Task<Feature> FeatureForAsync(AttachmentTarget target, CancellationToken cancellationToken) =>
        FindFeatureAsync(target.Dataset, target.Store, target.ObjectId, cancellationToken);

    /// <summary>
    /// Resolves one feature by its Esri <c>OBJECTID</c> — the identity column
    /// when the layer has one, otherwise the scan ordinal. An unknown object
    /// id is <c>not.found</c>.
    /// </summary>
    public static async Task<Feature> FindFeatureAsync(
        DatasetDescription dataset, IFeatureStore store, long objectId, CancellationToken cancellationToken)
    {
        var scheme = EsriObjectIdScheme.For(dataset);
        var batches = await store.ScanAsync(dataset.Id, cancellationToken);
        long ordinal = 0;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordinal++;
            if (!scheme.TryResolve(feature, ordinal, out var candidate))
            {
                throw GeoServicesErrors.ServerError(
                    $"The identity column of layer '{dataset.Id}' is not an integer.");
            }

            if (candidate == objectId)
            {
                return feature;
            }
        }

        throw NotFound($"Feature {objectId} does not exist in layer '{dataset.Id}'.");
    }

    /// <summary>
    /// Resolves the query targets: the requested object ids in request order,
    /// or every feature in scan order when no ids are given. Unknown object
    /// ids are <c>not.found</c>, never silently dropped.
    /// </summary>
    internal static async Task<IReadOnlyList<(long ObjectId, Feature Feature)>> ResolveAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        IReadOnlyList<long>? objectIds,
        CancellationToken cancellationToken)
    {
        if (objectIds is null)
        {
            return await ScanAsync(dataset, store, cancellationToken);
        }

        var targets = new List<(long, Feature)>(objectIds.Count);
        foreach (var objectId in objectIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            targets.Add((objectId, await FindFeatureAsync(dataset, store, objectId, cancellationToken)));
        }

        return targets;
    }

    private static async Task<IReadOnlyList<(long ObjectId, Feature Feature)>> ScanAsync(
        DatasetDescription dataset, IFeatureStore store, CancellationToken cancellationToken)
    {
        var scheme = EsriObjectIdScheme.For(dataset);
        var batches = await store.ScanAsync(dataset.Id, cancellationToken);
        var targets = new List<(long, Feature)>();
        long ordinal = 0;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordinal++;
            if (!scheme.TryResolve(feature, ordinal, out var objectId))
            {
                throw GeoServicesErrors.ServerError(
                    $"The identity column of layer '{dataset.Id}' is not an integer.");
            }

            targets.Add((objectId, feature));
        }

        return targets;
    }

    /// <summary>
    /// Parses an Esri id list (<c>objectIds</c>, <c>attachmentIds</c>): null
    /// when absent, otherwise the comma-separated integers. Malformed values
    /// are typed <c>invalid.arguments</c> naming the parameter.
    /// </summary>
    public static IReadOnlyList<long>? ParseIds(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var ids = new List<long>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
            {
                throw GeoServicesErrors.Invalid($"The '{parameter}' parameter must be a comma-separated list of integers, got '{value}'.");
            }

            ids.Add(id);
        }

        return ids;
    }

    private static EsriInteropException NotFound(string message) => new(EsriErrorCodes.NotFound, message);
}
