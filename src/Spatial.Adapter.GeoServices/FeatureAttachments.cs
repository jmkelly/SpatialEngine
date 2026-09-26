using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The attachment read surface (S4 query-attachments/attachments/content),
/// served on the <c>IFeatureAttachmentStore</c> face (ADR-0065, ADR-0066):
/// reads list the stored blobs per feature. The write surface lives in
/// <see cref="FeatureAttachmentWrites"/> and the shared OBJECTID resolution in
/// <see cref="FeatureAttachmentTargets"/>. Stores without blob support (demo,
/// ArcGIS REST, PostGIS until its sidecar lands) keep the ADR-0061 honesty:
/// empty reads and typed write rejects naming the missing face. Every method
/// observes its <see cref="CancellationToken"/> before touching state.
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
        var targets = await FeatureAttachmentTargets.ResolveAsync(dataset, store, objectIds, cancellationToken);
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
        FeatureAttachmentTargets.AttachmentTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var feature = await FeatureAttachmentTargets.FeatureForAsync(target, cancellationToken);
        return EsriJson.Value(new EsriAttachmentInfosResponse(
            await InfosForAsync(target.Dataset, target.Attachments, feature, cancellationToken)));
    }

    /// <summary>
    /// The per-attachment content resource: the stored bytes with their
    /// content type. Unknown features and unknown attachment ids are
    /// <c>not.found</c>; without a face no attachment can exist, so
    /// every id is <c>not.found</c>.
    /// </summary>
    public static async Task<IResult> ContentAsync(
        FeatureAttachmentTargets.AttachmentTarget target, long attachmentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var feature = await FeatureAttachmentTargets.FeatureForAsync(target, cancellationToken);
        if (target.Attachments is null)
        {
            throw new EsriInteropException(
                EsriErrorCodes.NotFound,
                $"Attachment {attachmentId} does not exist on feature {target.ObjectId}: the store exposes no attachment face.");
        }

        var content = await target.Attachments.GetAsync(target.Dataset.Id, feature.Id, attachmentId, cancellationToken);
        return Results.Bytes(content.Content, content.Descriptor.ContentType, content.Descriptor.Name);
    }

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
}

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
