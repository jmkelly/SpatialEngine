using Spatial.Interop.Esri;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The attachment surface (S4 query-attachments/add-attachment/…): the
/// <c>IFeatureAttachmentStore</c> capability exists (ADR-0064) but no layer
/// advertises <c>hasAttachments</c> yet — so reads truthfully report the
/// empty set and writes fail as typed <c>invalid.arguments</c> naming the
/// missing capability (ADR-0058). Serving the surface on the capability is
/// T-061; this surface stays honest and unadvertised until it lands.
/// </summary>
internal static class FeatureAttachments
{
    /// <summary>
    /// The empty attachment list: no attachment is stored for any feature,
    /// so <c>queryAttachments</c> and the per-feature <c>attachments</c>
    /// resource always report it.
    /// </summary>
    public static IResult Empty() => EsriJson.Value(new EsriAttachmentInfosResponse([]));

    /// <summary>Rejects an attachment write: there is no store to accept it.</summary>
    public static EsriInteropException WriteError(string operation) =>
        new(
            Spatial.Interop.Esri.EsriErrorCodes.InvalidParameters,
            $"The '{operation}' operation is not supported: the engine has no attachment store, so layer attachments cannot be added, updated or deleted (ADR-0058).");
}

/// <summary>One stored attachment descriptor (S4 attachment-infos shape).</summary>
internal sealed record EsriAttachmentInfo(
    long Id,
    string Name,
    string ContentType,
    long Size,
    string? Keywords = null);

/// <summary>The <c>queryAttachments</c> / per-feature <c>attachments</c> response.</summary>
internal sealed record EsriAttachmentInfosResponse(IReadOnlyList<EsriAttachmentInfo> AttachmentInfos);
