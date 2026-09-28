using System.Security.Cryptography;
using Spatial.Contracts.Providers;

namespace Spatial.Client;

/// <summary>
/// How <see cref="ResumableIngest"/> chunks a document and how hard it retries
/// (ADR-0088).
/// </summary>
/// <param name="ChunkSize">Bytes per request; the default is 8 MiB.</param>
/// <param name="UploadId">
/// A caller-chosen id, so a driver that is re-run resumes the upload it
/// started instead of staging the document again. The default stages a new one.
/// </param>
/// <param name="MaxAttempts">Attempts per chunk before the failure is the caller's.</param>
public sealed record ResumableUpload(
    long ChunkSize = 8L * 1024 * 1024,
    string? UploadId = null,
    int MaxAttempts = 3);

/// <summary>
/// Uploads a large document in resumable chunks (ADR-0088): stage, ask where
/// the staging got to, append from there, and ingest once every byte is in.
/// <para>
/// The driver is the honest part of the protocol. A chunk that fails in transit
/// is retried from the offset the host reports rather than from the client's
/// own count, and a document whose bytes are not the ones the digest was taken
/// over is refused by the host before anything is loaded.
/// </para>
/// <para>
/// The load itself is not chunked. Every byte is decoded and loaded in one
/// transaction at the ingest step, so a malformed row at the end of a resumed
/// upload still fails the whole load and leaves no dataset — resumability buys
/// the transport, not partial loads.
/// </para>
/// </summary>
public static class ResumableIngest
{
    /// <summary>
    /// Stages <paramref name="content"/> and ingests it, resuming from
    /// whatever the host already holds.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The content cannot be rewound. Resuming means re-reading from an
    /// offset, which a forward-only stream cannot do.
    /// </exception>
    public static async Task<IngestOutcome> UploadAsync(
        SpatialClient client,
        Stream content,
        IngestUpload upload,
        ResumableUpload? options = null,
        string? adminToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(upload);
        if (!content.CanSeek || !content.CanRead)
        {
            throw new ArgumentException(
                "A resumable upload needs a seekable stream: it re-reads the document from the offset the host reports.",
                nameof(content));
        }

        var settings = options ?? new ResumableUpload();
        var total = content.Length;
        var digest = await DigestAsync(content, cancellationToken).ConfigureAwait(false);
        var staged = await client.Maps
            .StartUploadAsync(settings.UploadId, total, digest, adminToken, cancellationToken)
            .ConfigureAwait(false);

        var state = await client.Maps.GetUploadAsync(staged.UploadId, adminToken, cancellationToken).ConfigureAwait(false);
        while (!state.Complete)
        {
            state = await SendAsync(client, staged.UploadId, content, total, digest, state, settings, adminToken, cancellationToken)
                .ConfigureAwait(false);
        }

        return await client.Maps.IngestUploadAsync(staged.UploadId, upload, adminToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends the next chunk, retrying a transport failure from the offset the
    /// host reports rather than from the one the client believed.
    /// </summary>
    private static async Task<UploadState> SendAsync(
        SpatialClient client,
        string uploadId,
        Stream content,
        long total,
        string digest,
        UploadState state,
        ResumableUpload settings,
        string? adminToken,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var chunk = Read(content, state.Received, settings.ChunkSize);
            try
            {
                return await client.Maps
                    .AppendUploadAsync(uploadId, chunk, state.Received, total, digest, adminToken, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < settings.MaxAttempts && IsTransport(exception))
            {
                // The chunk may or may not have landed, so the next attempt
                // re-reads from the host's offset, never from this one.
                state = await client.Maps.GetUploadAsync(uploadId, adminToken, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The bytes from <paramref name="offset"/> to the end of the document, or
    /// one chunk of them. The digest and length are taken from the whole
    /// document, so a short read would silently stage a fragment; there is
    /// none.
    /// </summary>
    private static MemoryStream Read(Stream content, long offset, long chunkSize)
    {
        if (offset > 0)
        {
            content.Position = offset;
        }

        var length = (int)Math.Min(chunkSize, content.Length - offset);
        var chunk = new byte[length];
        content.ReadExactly(chunk, 0, length);
        return new MemoryStream(chunk, writable: false);
    }

    private static async Task<string> DigestAsync(Stream content, CancellationToken cancellationToken)
    {
        var start = content.Position;
        var digest = await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false);
        content.Position = start;
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// Whether a failure is worth retrying. A chunk that fails in transit, or
    /// a server-side answer, is: the host may or may not have taken it, and the
    /// retry re-reads from whatever offset it reports. A structured client
    /// error — a refused offset, a faulted upload — is not, because it would
    /// fail identically however many times it were sent.
    /// </summary>
    private static bool IsTransport(Exception exception) =>
        exception switch
        {
            HttpRequestException or IOException => true,
            SpatialClientException { StatusCode: >= 500 } => true,
            TaskCanceledException canceled => !canceled.CancellationToken.IsCancellationRequested,
            _ => false,
        };
}
