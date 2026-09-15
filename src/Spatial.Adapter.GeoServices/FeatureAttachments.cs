using Spatial.Core.Features;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The attachment surface (S4 query-attachments/add-attachment/…), served on
/// the <c>IFeatureAttachmentStore</c> face (ADR-0065, ADR-0066): reads
/// list the stored blobs per feature, writes put/replace/delete them, and
/// the layer advertises <c>hasAttachments</c> exactly when the store exposes
/// the face. Stores without blob support (demo, ArcGIS REST, PostGIS
/// until its sidecar lands) keep the ADR-0061 honesty: empty reads and typed
/// write rejects naming the missing face. Every method observes its
/// <see cref="CancellationToken"/> before touching state.
/// </summary>
internal static class FeatureAttachments
{
    /// <summary>
    /// The layer-level <c>queryAttachments</c> (S4): one group per requested
    /// feature (every feature when <paramref name="objectIds"/> is null),
    /// each carrying its stored attachment infos in id order. Without a
    /// face the truthful empty set is reported.
    /// </summary>
    public static async Task<IResult> QueryAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        IFeatureAttachmentStore? attachments,
        IReadOnlyList<long>? objectIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var targets = await ResolveTargetsAsync(dataset, store, objectIds, cancellationToken);
        var groups = new List<EsriAttachmentGroup>(targets.Count);
        foreach (var (objectId, feature) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            groups.Add(new EsriAttachmentGroup(objectId, await InfosForAsync(dataset, attachments, feature, cancellationToken)));
        }

        return EsriJson.Value(new EsriAttachmentGroupsResponse(groups));
    }

    /// <summary>
    /// The per-feature <c>attachments</c> resource: the stored attachment
    /// infos for one feature in id order, or the truthful empty set without
    /// a face. An unknown feature is <c>not.found</c>.
    /// </summary>
    public static async Task<IResult> InfosAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        IFeatureAttachmentStore? attachments,
        long objectId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var feature = await FindFeatureAsync(dataset, store, objectId, cancellationToken);
        return EsriJson.Value(new EsriAttachmentInfosResponse(
            await InfosForAsync(dataset, attachments, feature, cancellationToken)));
    }

    /// <summary>
    /// The per-attachment content resource: the stored bytes with their
    /// content type. Unknown features and unknown attachment ids are
    /// <c>not.found</c>; without a face no attachment can exist, so
    /// every id is <c>not.found</c>.
    /// </summary>
    public static async Task<IResult> ContentAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        IFeatureAttachmentStore? attachments,
        long objectId,
        long attachmentId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var feature = await FindFeatureAsync(dataset, store, objectId, cancellationToken);
        if (attachments is null)
        {
            throw NotFound($"Attachment {attachmentId} does not exist on feature {objectId}: the store exposes no attachment face.");
        }

        var content = await attachments.GetAsync(dataset.Id, feature.Id, attachmentId, cancellationToken);
        return Results.Bytes(content.Content, content.Descriptor.ContentType, content.Descriptor.Name);
    }

    /// <summary>
    /// The per-feature <c>addAttachment</c>: stores one blob and reports its
    /// assigned attachment id. Without a face the write is rejected as
    /// typed <c>invalid.arguments</c> naming the missing face.
    /// </summary>
    public static async Task<IResult> AddAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        IFeatureAttachmentStore? attachments,
        long objectId,
        AttachmentUpload upload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        cancellationToken.ThrowIfCancellationRequested();
        var feature = await FindFeatureAsync(dataset, store, objectId, cancellationToken);
        if (attachments is null)
        {
            throw WriteError("addAttachment");
        }

        var descriptor = await attachments.AddAsync(
            dataset.Id, feature.Id, upload.Name, upload.ContentType, upload.Content, upload.Keywords, cancellationToken);
        return EsriJson.Value(new EsriAddAttachmentResponse(new EsriAddAttachmentResult(descriptor.Id, true)));
    }

    /// <summary>
    /// The per-feature <c>updateAttachment</c>: replaces one attachment's
    /// metadata and bytes, keeping its identity. An unknown attachment id is
    /// <c>not.found</c>.
    /// </summary>
    public static async Task<IResult> UpdateAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        IFeatureAttachmentStore? attachments,
        long objectId,
        long attachmentId,
        AttachmentUpload upload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        cancellationToken.ThrowIfCancellationRequested();
        var feature = await FindFeatureAsync(dataset, store, objectId, cancellationToken);
        if (attachments is null)
        {
            throw WriteError("updateAttachment");
        }

        var descriptor = await attachments.UpdateAsync(
            dataset.Id, feature.Id, attachmentId, upload.Name, upload.ContentType, upload.Content, upload.Keywords, cancellationToken);
        return EsriJson.Value(new EsriUpdateAttachmentResponse(new EsriUpdateAttachmentResult(descriptor.Id, true)));
    }

    /// <summary>
    /// The per-feature <c>deleteAttachments</c>: one result per attachment id
    /// in input order, so a partially successful batch is reported per
    /// attachment rather than as one failure (the <c>applyEdits</c> pattern).
    /// </summary>
    public static async Task<IResult> DeleteAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        IFeatureAttachmentStore? attachments,
        long objectId,
        IReadOnlyList<long> attachmentIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachmentIds);
        cancellationToken.ThrowIfCancellationRequested();
        var feature = await FindFeatureAsync(dataset, store, objectId, cancellationToken);
        if (attachments is null)
        {
            throw WriteError("deleteAttachments");
        }

        var outcomes = await attachments.DeleteAsync(dataset.Id, feature.Id, attachmentIds, cancellationToken);
        return EsriJson.Value(new EsriDeleteAttachmentsResponse(outcomes
            .Select(outcome => outcome.Succeeded
                ? new EsriDeleteAttachmentResult(outcome.Id, true)
                : new EsriDeleteAttachmentResult(
                    outcome.Id, false,
                    new EsriError(
                        EsriErrorMapper.EditCodeFor(outcome.ErrorCode),
                        outcome.ErrorMessage ?? $"Attachment {outcome.Id} could not be deleted.",
                        [])))
            .ToArray()));
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

    /// <summary>Rejects an attachment write: the store exposes no blob face.</summary>
    public static EsriInteropException WriteError(string operation) =>
        new(
            EsriErrorCodes.InvalidParameters,
            $"The '{operation}' operation is not supported on this layer: the store exposes no attachment face, so layer attachments cannot be added, updated or deleted (ADR-0065).");

    private static async Task<IReadOnlyList<EsriAttachmentInfo>> InfosForAsync(
        DatasetDescription dataset,
        IFeatureAttachmentStore? attachments,
        Feature feature,
        CancellationToken cancellationToken)
    {
        if (attachments is null)
        {
            return [];
        }

        var descriptors = await attachments.ListAsync(dataset.Id, feature.Id, cancellationToken);
        return descriptors.Select(ToInfo).ToArray();
    }

    private static EsriAttachmentInfo ToInfo(FeatureAttachmentDescriptor descriptor) =>
        new(descriptor.Id, descriptor.Name, descriptor.ContentType, descriptor.Size, descriptor.Keywords);

    /// <summary>
    /// Resolves the query targets: the requested object ids in request order,
    /// or every feature in scan order when no ids are given. Unknown object
    /// ids are <c>not.found</c>, never silently dropped.
    /// </summary>
    private static async Task<IReadOnlyList<(long ObjectId, Feature Feature)>> ResolveTargetsAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        IReadOnlyList<long>? objectIds,
        CancellationToken cancellationToken)
    {
        if (objectIds is null)
        {
            return await ScanTargetsAsync(dataset, store, cancellationToken);
        }

        var targets = new List<(long, Feature)>(objectIds.Count);
        foreach (var objectId in objectIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            targets.Add((objectId, await FindFeatureAsync(dataset, store, objectId, cancellationToken)));
        }

        return targets;
    }

    private static async Task<IReadOnlyList<(long ObjectId, Feature Feature)>> ScanTargetsAsync(
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
    /// Resolves one feature by its Esri <c>OBJECTID</c> — the identity column
    /// when the layer has one, otherwise the scan ordinal — exactly as
    /// <c>query</c> and the feature resource do, so attachments agree with
    /// <c>returnIdsOnly</c>. An unknown object id is <c>not.found</c>.
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

    private static EsriInteropException NotFound(string message) => new(EsriErrorCodes.NotFound, message);
}

/// <summary>One uploaded attachment: the multipart file part with its metadata.</summary>
internal sealed record AttachmentUpload(string Name, string ContentType, byte[] Content, string? Keywords = null);

/// <summary>One stored attachment descriptor (S4 attachment-info shape).</summary>
internal sealed record EsriAttachmentInfo(
    long Id,
    string Name,
    string ContentType,
    long Size,
    string? Keywords = null);

/// <summary>The per-feature <c>attachments</c> response.</summary>
internal sealed record EsriAttachmentInfosResponse(IReadOnlyList<EsriAttachmentInfo> AttachmentInfos);

/// <summary>One feature's attachments inside a <c>queryAttachments</c> response.</summary>
internal sealed record EsriAttachmentGroup(long ParentObjectId, IReadOnlyList<EsriAttachmentInfo> AttachmentInfos);

/// <summary>The layer-level <c>queryAttachments</c> response (S4).</summary>
internal sealed record EsriAttachmentGroupsResponse(IReadOnlyList<EsriAttachmentGroup> AttachmentGroups);

/// <summary>The <c>addAttachment</c> response: the assigned attachment id.</summary>
internal sealed record EsriAddAttachmentResponse(EsriAddAttachmentResult AddAttachmentResult);

/// <summary>The assigned attachment id of a successful add.</summary>
internal sealed record EsriAddAttachmentResult(long ObjectId, bool Success);

/// <summary>The <c>updateAttachment</c> response: the kept attachment id.</summary>
internal sealed record EsriUpdateAttachmentResponse(EsriUpdateAttachmentResult UpdateAttachmentResult);

/// <summary>The kept attachment id of a successful update.</summary>
internal sealed record EsriUpdateAttachmentResult(long ObjectId, bool Success);

/// <summary>The <c>deleteAttachments</c> response: one result per id in input order.</summary>
internal sealed record EsriDeleteAttachmentsResponse(IReadOnlyList<EsriDeleteAttachmentResult> DeleteAttachmentResults);

/// <summary>One attachment delete result; failures carry the typed error.</summary>
internal sealed record EsriDeleteAttachmentResult(long ObjectId, bool Success, EsriError? Error = null);
