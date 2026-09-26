using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The attachment write surface (S4 add-attachment/update-attachment/
/// delete-attachments, ADR-0065/0066): the per-feature blob mutations and the
/// result envelopes they report. Split out of <see cref="FeatureAttachments"/>
/// so the read surface and the write surface carry their own fan-out.
/// </summary>
internal static class FeatureAttachmentWrites
{
    /// <summary>
    /// The per-feature <c>addAttachment</c>: stores one blob and reports its
    /// assigned attachment id. Without a face the write is rejected as
    /// typed <c>invalid.arguments</c> naming the missing face.
    /// </summary>
    public static async Task<IResult> AddAsync(
        FeatureAttachmentTargets.AttachmentTarget target, FeatureAttachmentWrite upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        cancellationToken.ThrowIfCancellationRequested();
        var feature = await FeatureAttachmentTargets.FeatureForAsync(target, cancellationToken);
        if (target.Attachments is null)
        {
            throw WriteError("addAttachment");
        }

        var descriptor = await target.Attachments.AddAsync(target.Dataset.Id, feature.Id, upload, cancellationToken);
        return EsriJson.Value(new EsriAddAttachmentResponse(new EsriAddAttachmentResult(descriptor.Id, true)));
    }

    /// <summary>
    /// The per-feature <c>updateAttachment</c>: replaces one attachment's
    /// metadata and bytes, keeping its identity. An unknown attachment id is
    /// <c>not.found</c>.
    /// </summary>
    public static async Task<IResult> UpdateAsync(
        FeatureAttachmentTargets.AttachmentTarget target, long attachmentId, FeatureAttachmentWrite upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(upload);
        cancellationToken.ThrowIfCancellationRequested();
        var feature = await FeatureAttachmentTargets.FeatureForAsync(target, cancellationToken);
        if (target.Attachments is null)
        {
            throw WriteError("updateAttachment");
        }

        var descriptor = await target.Attachments.UpdateAsync(
            target.Dataset.Id, feature.Id, attachmentId, upload, cancellationToken);
        return EsriJson.Value(new EsriUpdateAttachmentResponse(new EsriUpdateAttachmentResult(descriptor.Id, true)));
    }

    /// <summary>
    /// The per-feature <c>deleteAttachments</c>: one result per attachment id
    /// in input order, so a partially successful batch is reported per
    /// attachment rather than as one failure (the <c>applyEdits</c> pattern).
    /// </summary>
    public static async Task<IResult> DeleteAsync(
        FeatureAttachmentTargets.AttachmentTarget target, IReadOnlyList<long> attachmentIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachmentIds);
        cancellationToken.ThrowIfCancellationRequested();
        var feature = await FeatureAttachmentTargets.FeatureForAsync(target, cancellationToken);
        if (target.Attachments is null)
        {
            throw WriteError("deleteAttachments");
        }

        var outcomes = await target.Attachments.DeleteAsync(target.Dataset.Id, feature.Id, attachmentIds, cancellationToken);
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

    /// <summary>Rejects an attachment write: the store exposes no blob face.</summary>
    public static EsriInteropException WriteError(string operation) =>
        new(
            EsriErrorCodes.InvalidParameters,
            $"The '{operation}' operation is not supported on this layer: the store exposes no attachment face, so layer attachments cannot be added, updated or deleted (ADR-0065).");
}
