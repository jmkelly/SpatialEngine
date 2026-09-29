using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Resolving the feature a per-feature resource addresses: the Esri
/// <c>OBJECTID</c> to its feature, exactly as <c>query</c> and the feature
/// resource do, so attachments agree with <c>returnIdsOnly</c>. Shared by the
/// per-feature (object) resource and every attachment resource, which is why
/// it is its own type rather than a preamble each of them repeats (ADR-0040).
/// </summary>
/// <remarks>
/// A layer whose <c>OBJECTID</c> is a durable identity column resolves its
/// target through the store's <see cref="IFeatureLookup"/> — one targeted read
/// of the requested ids, never a whole-dataset scan (ADR-0038, ADR-0112). A
/// layer whose object id is the scan ordinal has no durable identity for a
/// lookup to read, so it keeps the scan, which is also the numbering its
/// object id is counted from.
/// <para>
/// A targeted read that <em>resolves</em> a target ends the read: the row is
/// keyed by the <c>OBJECTID</c> it carries, so it is the requested feature and
/// not a row that happens to share the id the lookup was asked with. A read
/// that does not resolve one is not evidence of absence — a store is free to
/// key its features by something other than the layer's identity column (a
/// source-identity ingest numbers features as it reads them) — so those
/// targets, and only those, are resolved by the scan, which is what every
/// target was resolved by before. The answer is the same either way; what
/// changes is whether a store that can key by identity is asked to.
/// </para>
/// </remarks>
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
        if (LookupFor(dataset, store) is { } lookup)
        {
            var found = await lookup
                .GetAsync(dataset.Id, [EsriObjectIdScheme.ToFeatureId(objectId)], cancellationToken)
                .ConfigureAwait(false);
            if (Index(dataset, found).GetValueOrDefault(objectId) is { } hit)
            {
                return hit;
            }
        }

        return await ScanForAsync(dataset, store, objectId, cancellationToken);
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

        // One targeted read for the whole request rather than one per id: a
        // per-object resource is a keyed read, and asking for it n times is
        // the scan it was meant to replace, in pieces.
        var index = LookupFor(dataset, store) is { } lookup
            ? Index(
                dataset,
                await lookup
                    .GetAsync(dataset.Id, objectIds.Distinct().Select(EsriObjectIdScheme.ToFeatureId).ToArray(), cancellationToken)
                    .ConfigureAwait(false))
            : [];

        var targets = new List<(long, Feature)>(objectIds.Count);
        foreach (var objectId in objectIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            targets.Add((
                objectId,
                index.GetValueOrDefault(objectId) is { } hit
                    ? hit
                    : await ScanForAsync(dataset, store, objectId, cancellationToken)));
        }

        return targets;
    }

    /// <summary>
    /// The store's read-by-identity face for this layer, or null when the layer
    /// cannot use one. The condition is about identity rather than dialect
    /// support: the <c>OBJECTID</c> a lookup resolves has to be the layer's own
    /// durable key, not an ordinal counted from a scan.
    /// </summary>
    private static IFeatureLookup? LookupFor(DatasetDescription dataset, IFeatureStore store) =>
        EsriObjectIdScheme.For(dataset).IsIdentity && dataset.IdColumns is [var _]
            ? store as IFeatureLookup
            : null;

    /// <summary>
    /// The fetched features by the <c>OBJECTID</c> each one <em>carries</em> —
    /// its identity column read off the feature, never the id the lookup was
    /// asked with. Keying by the request would let a store whose
    /// <see cref="Feature.Id"/> is not the identity column's value (a
    /// source-identity ingest numbers features as it reads them) answer with a
    /// row that belongs to another object; keyed this way such a row lands
    /// under its own id, the request misses, and the scan decides.
    /// </summary>
    private static Dictionary<long, Feature> Index(DatasetDescription dataset, IReadOnlyList<Feature> found)
    {
        var scheme = EsriObjectIdScheme.For(dataset);
        var index = new Dictionary<long, Feature>(found.Count);
        foreach (var feature in found)
        {
            if (!scheme.TryResolve(feature, 0, out var objectId))
            {
                throw GeoServicesErrors.ServerError(
                    $"The identity column of layer '{dataset.Id}' is not an integer.");
            }

            index[objectId] = feature;
        }

        return index;
    }

    /// <summary>
    /// One feature by its object id, counted off the scan — the numbering an
    /// ordinal-keyed layer's <c>OBJECTID</c> is, and the answer for a target a
    /// targeted read did not resolve. An unknown object id is
    /// <c>not.found</c>.
    /// </summary>
    private static async Task<Feature> ScanForAsync(
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
