using Spatial.Core.Features;

namespace Spatial.PluginSdk.Providers;

/// <summary>
/// One stored attachment descriptor: the core-typed, provider-neutral face
/// of an S4 attachment-info (numeric identity, file name, MIME type, byte
/// size and optional keywords). The bytes themselves never cross this
/// contract except through <see cref="FeatureAttachmentContent"/>; Esri
/// shaping stays in the adapter.
/// </summary>
public sealed record FeatureAttachmentDescriptor(long Id, string Name, string ContentType, long Size, string? Keywords = null);

/// <summary>
/// One stored attachment with its provider-owned bytes. The store hands out
/// a defensive copy on every read and copies on every write, so a caller can
/// never mutate the stored bytes through a previously returned array.
/// </summary>
public sealed record FeatureAttachmentContent(FeatureAttachmentDescriptor Descriptor, byte[] Content);

/// <summary>
/// The outcome of one attachment delete, mirroring
/// <see cref="FeatureEditOutcome"/> (ADR-0037): deletes report per attachment
/// id in input order, so a partially successful batch is reported per
/// attachment rather than as one failure.
/// </summary>
public sealed record FeatureAttachmentOutcome(long Id, bool Succeeded, string? ErrorCode = null, string? ErrorMessage = null)
{
    /// <summary>A successful delete.</summary>
    public static FeatureAttachmentOutcome Success(long id) => new(id, true);

    /// <summary>A failed delete carrying the engine error code and message.</summary>
    public static FeatureAttachmentOutcome Failure(long id, string code, string message) => new(id, false, code, message);
}

/// <summary>
/// Feature-attachment blobs over a store (T-060), the attachment sibling of
/// <see cref="IFeatureEditStore"/>. An additive capability keyed by layer
/// dataset and feature identity: a store that can hold per-feature blobs
/// implements it, read-only or blob-less stores simply omit it and the facade
/// keeps reporting empty reads with typed write rejects (ADR-0061 §5).
/// Attachment identities are positive integers assigned per feature, starting
/// at one, so the adapter can project them as Esri attachment ids directly.
///
/// <para>Errors are structured <see cref="SpatialException"/> codes: an
/// unknown dataset or feature is <c>not.found</c>, an empty name, null
/// content or over-quota bytes are <c>invalid.arguments</c>, and a missing
/// attachment id on get/update is <c>not.found</c> while on delete it is a
/// per-id <see cref="FeatureAttachmentOutcome"/> failure. Every method
/// observes its <see cref="CancellationToken"/> before touching state.
/// Authentication and quota values are host concerns layered above this
/// contract (ADR-0065): the store enforces its own byte cap, the adapter
/// gates the writes.</para>
/// </summary>
public interface IFeatureAttachmentStore
{
    /// <summary>Lists the attachments stored for one feature, in id order; an empty list means none are stored.</summary>
    Task<IReadOnlyList<FeatureAttachmentDescriptor>> ListAsync(
        string dataset, FeatureId featureId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores one blob for a feature and returns its assigned descriptor. An
    /// empty content type defaults to <c>application/octet-stream</c>.
    /// </summary>
    Task<FeatureAttachmentDescriptor> AddAsync(
        string dataset, FeatureId featureId, string name, string contentType, byte[] content,
        string? keywords = null, CancellationToken cancellationToken = default);

    /// <summary>Returns one stored attachment with its bytes.</summary>
    Task<FeatureAttachmentContent> GetAsync(
        string dataset, FeatureId featureId, long attachmentId, CancellationToken cancellationToken = default);

    /// <summary>Replaces one stored attachment's metadata and bytes, keeping its identity.</summary>
    Task<FeatureAttachmentDescriptor> UpdateAsync(
        string dataset, FeatureId featureId, long attachmentId, string name, string contentType, byte[] content,
        string? keywords = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes attachments by identity, one outcome per id in input order.</summary>
    Task<IReadOnlyList<FeatureAttachmentOutcome>> DeleteAsync(
        string dataset, FeatureId featureId, IReadOnlyList<long> attachmentIds, CancellationToken cancellationToken = default);
}
