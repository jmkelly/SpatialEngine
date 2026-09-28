namespace Spatial.Contracts.Providers;

/// <summary>
/// One append to a staged upload: the byte offset the chunk belongs at, and —
/// when the client knows them — the total size of the whole document and its
/// SHA-256 digest.
/// <para>
/// The offset is the whole resume protocol (ADR-0089): a client that lost its
/// own state asks the staging how far it got rather than restarting.
/// </para>
/// </summary>
public sealed record UploadAppend(long Offset, long? TotalBytes = null, string? Sha256 = null);

/// <summary>
/// How much of a staged upload has landed, and whether it is complete.
/// <para>
/// <see cref="Complete"/> is deliberately <em>not</em> "no more bytes are
/// coming": it is true only when a declared total has been reached, so a
/// partial upload can never be presented as a loadable one.
/// </para>
/// </summary>
/// <param name="UploadId">The opaque identifier the staging is addressed by.</param>
/// <param name="Received">Bytes staged so far.</param>
/// <param name="TotalBytes">The declared size of the whole document, when known.</param>
/// <param name="Complete">Whether every declared byte has arrived.</param>
/// <param name="Sha256">The declared digest of the whole document, when known.</param>
/// <param name="Fault">
/// Why the last append was refused — a digest that does not match, for
/// instance. A faulted upload is not complete and cannot become complete by
/// appending more: its bytes are not the declared document, so the caller
/// discards it and stages a new one.
/// </param>
public sealed record UploadState(
    string UploadId,
    long Received,
    long? TotalBytes = null,
    bool Complete = false,
    string? Sha256 = null,
    string? Fault = null);

/// <summary>
/// Byte-level staging for an upload too large to send in one request
/// (ADR-0089). A client appends chunks, asks where the staging got to, and
/// finally ingests the staged bytes; the feature-level load stays one atomic
/// transaction, because what is chunked is the transport, not the load.
/// <para>
/// The faces are byte-level on purpose. A staged upload is a pending document,
/// not a pending dataset: nothing here creates, extends or queries a dataset,
/// and a store that cannot stage bytes has no reason to implement it.
/// </para>
/// </summary>
public interface IUploadStaging
{
    /// <summary>
    /// Opens a staged upload, optionally at a caller-chosen
    /// <paramref name="uploadId"/>. Re-opening an existing id returns its
    /// current state rather than truncating it, so a client whose "create"
    /// response was lost can recover; re-declaring a different total or digest
    /// for the same id is <c>invalid.arguments</c>.
    /// </summary>
    Task<UploadState> StartAsync(
        string? uploadId,
        long? totalBytes = null,
        string? sha256 = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends <paramref name="chunk"/> at <see cref="UploadAppend.Offset"/>.
    /// An offset below the staged length is accepted when the overlapping
    /// bytes are identical (a lost acknowledgement, not a new document); an
    /// offset above it is <c>invalid.arguments</c> naming the offset to resume
    /// from. A refused append leaves the staged upload exactly as it was.
    /// </summary>
    Task<UploadState> AppendAsync(
        string uploadId, Stream chunk, UploadAppend append, CancellationToken cancellationToken = default);

    /// <summary>How much of a staged upload has landed.</summary>
    /// <exception cref="SpatialException"><c>not.found</c> when no such upload is staged.</exception>
    Task<UploadState> DescribeAsync(string uploadId, CancellationToken cancellationToken = default);

    /// <summary>Every staged upload, oldest first.</summary>
    Task<IReadOnlyList<UploadState>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The staged bytes, for an ingest to decode. The caller owns the stream.
    /// </summary>
    /// <exception cref="SpatialException"><c>not.found</c> when no such upload is staged.</exception>
    Task<Stream> OpenAsync(string uploadId, CancellationToken cancellationToken = default);

    /// <summary>Discards a staged upload and its bytes; whether one existed.</summary>
    Task<bool> DiscardAsync(string uploadId, CancellationToken cancellationToken = default);
}
