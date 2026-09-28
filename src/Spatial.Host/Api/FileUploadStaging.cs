using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Spatial.Contracts;
using Spatial.Contracts.Providers;

namespace Spatial.Host.Api;

/// <summary>
/// The file-backed <see cref="IUploadStaging"/> the host serves resumable
/// uploads from (ADR-0083). One directory, one <c>.part</c> file of staged
/// bytes and one <c>.json</c> of state per upload.
/// <para>
/// The length of the <c>.part</c> file is the truth for how far an upload
/// got, not the recorded state: an append that is interrupted — a cancelled
/// request, a killed process — leaves bytes the client believes were not
/// accepted, and the next append has to see them. Bytes are only ever appended
/// after the whole chunk has been read within the caps and matched against
/// whatever is already staged, so a refused chunk changes nothing.
/// </para>
/// </summary>
public sealed class FileUploadStaging : IUploadStaging
{
    private const string PartSuffix = ".part";
    private const string StateSuffix = ".json";

    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _clock;

    /// <param name="options">Where the staging lives and how long it is kept.</param>
    /// <param name="maxBytes">
    /// The bound on a whole staged document, in practice
    /// <c>Spatial:Ingest:MaxBytes</c>.
    /// </param>
    /// <param name="clock">The clock expiry is measured against.</param>
    public FileUploadStaging(UploadOptions options, long maxBytes, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        MaxBytes = maxBytes;
        _clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(Options.ResolvePath());
    }

    /// <summary>Where the staging lives and how long it is kept.</summary>
    public UploadOptions Options { get; }

    /// <summary>The bound on a whole staged document, in bytes.</summary>
    public long MaxBytes { get; }

    private const int MaxIdLength = 64;

    /// <summary>Upload ids address a file, so they are tokens, not names.</summary>
    private static bool IsSafeId(string uploadId) =>
        uploadId.Length is > 0 and <= MaxIdLength
        && uploadId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public async Task<UploadState> StartAsync(
        string? uploadId, long? totalBytes = null, string? sha256 = null, CancellationToken cancellationToken = default)
    {
        var id = string.IsNullOrWhiteSpace(uploadId) ? NewId() : RequireSafeId(uploadId);
        var total = RequireTotal(totalBytes);
        var digest = RequireDigest(sha256);
        Prune();
        var existing = Read(id);
        if (existing is not null)
        {
            // A repeated start is how a client whose create response was lost
            // recovers its upload, so it must never truncate what is staged.
            return Describe(Live(existing) with
            {
                TotalBytes = RequireSame(existing.TotalBytes, total, "total size"),
                Sha256 = RequireSame(existing.Sha256, digest, "digest"),
            });
        }

        var state = new StagedUpload(id, 0, total, digest, null, _clock.GetUtcNow());
        await WriteAsync(id, state, cancellationToken).ConfigureAwait(false);
        return Describe(state);
    }

    public async Task<UploadState> AppendAsync(
        string uploadId, Stream chunk, UploadAppend append, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(append);
        var id = RequireSafeId(uploadId);
        var state = Read(id) ?? throw Missing(id);
        if (state.Fault is { } existingFault)
        {
            throw SpatialException.BadArguments(
                $"Upload '{id}' is faulted ({existingFault}); discard it and stage the document again.");
        }

        var total = RequireSame(state.TotalBytes, RequireTotal(append.TotalBytes), "total size");
        var digest = RequireSame(state.Sha256, RequireDigest(append.Sha256), "digest");
        if (append.Offset < 0)
        {
            throw SpatialException.BadArguments($"The upload offset must not be negative, got {append.Offset}.");
        }

        var received = Length(id);
        if (append.Offset > received)
        {
            throw SpatialException.BadArguments(
                $"Upload '{id}' holds {received} byte(s); the chunk was addressed at {append.Offset}. Resume from {received}.");
        }

        var limit = total is { } declared ? Math.Min(declared, MaxBytes) : MaxBytes;
        // Room is measured from the offset the chunk was addressed at, not from
        // the staged length: a chunk addressed below the staged length repeats
        // bytes that are already there, and refusing it for not fitting would
        // turn the lost-acknowledgement resume into a dead end. The bytes it
        // actually adds are still bounded — `received + (room - overlap)` never
        // exceeds `limit`, so the declared total and the cap both still hold.
        var staged = await ReceiveAsync(id, chunk, limit - append.Offset, append.Offset, cancellationToken).ConfigureAwait(false);
        await VerifyOverlapAsync(id, append.Offset, staged, cancellationToken).ConfigureAwait(false);

        // The chunk's first byte belongs at `offset`, so the bytes before the
        // staged length are the re-sent overlap and are not written twice.
        var after = Append(id, staged, received - append.Offset);
        var completed = total is { } final && after == final;
        var fault = (string?)null;
        if (completed && digest is { } expected && !await MatchesAsync(id, expected, cancellationToken).ConfigureAwait(false))
        {
            // The staged bytes are not the document the client declared, so no
            // amount of appending will make them so. The upload is faulted and
            // stays staged for inspection until it is discarded.
            fault = $"the staged bytes do not match the declared SHA-256 digest {expected}";
            await WriteAsync(id, state with { Received = after, TotalBytes = total, Sha256 = digest, Fault = fault, Updated = _clock.GetUtcNow() }, cancellationToken)
                .ConfigureAwait(false);
            throw SpatialException.BadArguments(
                $"Upload '{id}' does not match its declared SHA-256 digest; the staged bytes are not the declared document. Discard the upload and stage it again.");
        }

        await WriteAsync(
            id,
            new StagedUpload(id, after, total, digest, fault, _clock.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
        return new UploadState(id, after, total, after == total && fault is null, digest, fault);
    }

    public Task<UploadState> DescribeAsync(string uploadId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Describe(Live(Read(RequireSafeId(uploadId)) ?? throw Missing(uploadId))));

    public Task<IReadOnlyList<UploadState>> ListAsync(CancellationToken cancellationToken = default)
    {
        Prune();
        var states = Directory.EnumerateFiles(Options.ResolvePath(), $"*{StateSuffix}")
            .Select(path => Read(Path.GetFileNameWithoutExtension(path)))
            .OfType<StagedUpload>()
            .Select(Describe)
            .OrderBy(state => state.UploadId, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult<IReadOnlyList<UploadState>>(states);
    }

    public Task<Stream> OpenAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        var id = RequireSafeId(uploadId);
        if (Read(id) is null || !File.Exists(Part(id)))
        {
            throw Missing(id);
        }

        return Task.FromResult<Stream>(new FileStream(
            Part(id), FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public Task<bool> DiscardAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        var id = RequireSafeId(uploadId);
        var existed = Read(id) is not null;
        Delete(id);
        return Task.FromResult(existed);
    }

    // ---- staging one incoming chunk -------------------------------------------------

    /// <summary>
    /// Reads the incoming chunk to a scratch file, refusing anything that would
    /// take the staged document past its declared total or the byte cap. The
    /// staged upload is untouched until the whole chunk is in hand.
    /// </summary>
    private async Task<string> ReceiveAsync(string id, Stream chunk, long room, long offset, CancellationToken cancellationToken)
    {
        if (room < 0)
        {
            throw SpatialException.BadArguments(
                $"Upload '{id}' is already at the maximum of {MaxBytes} byte(s) this host accepts.");
        }
        var scratch = Scratch(id);
        try
        {
            await CopyAsync(id, chunk, scratch, room, offset, cancellationToken).ConfigureAwait(false);
            return scratch;
        }
        catch
        {
            Remove(scratch);
            throw;
        }
    }

    private static async Task<long> CopyAsync(string id, Stream source, string scratch, long room, long offset, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        await using var target = new FileStream(scratch, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.Asynchronous);
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > room)
            {
                throw SpatialException.BadArguments(
                    $"The chunk does not fit: upload '{id}' accepts at most {room} more byte(s) from offset {offset}.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        return total;
    }

    /// <summary>
    /// Compares the bytes an overlapping chunk re-sends against what is already
    /// staged. A client whose acknowledgement was lost resends from an earlier
    /// offset; that is only the same document if the overlap is identical.
    /// </summary>
    private async Task VerifyOverlapAsync(
        string id, long offset, string staged, CancellationToken cancellationToken)
    {
        var overlap = Math.Min(Length(id) - offset, new FileInfo(staged).Length);
        if (overlap <= 0)
        {
            return;
        }

        var existing = new byte[overlap];
        var fresh = new byte[overlap];
        await using var stagedFile = new FileStream(Part(id), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var incoming = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
        stagedFile.Position = offset;
        await ReadExactlyAsync(stagedFile, existing, cancellationToken).ConfigureAwait(false);
        await ReadExactlyAsync(incoming, fresh, cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < overlap; index++)
        {
            if (existing[index] != fresh[index])
            {
                throw SpatialException.BadArguments(
                    $"The chunk re-sends bytes that differ from what is already staged at offset {offset + index}.");
            }
        }
    }

    private static async Task ReadExactlyAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await source.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new IOException("The staged upload ended before the overlapping chunk did.");
            }

            read += count;
        }
    }

    /// <summary>Appends the part of the chunk beyond the overlap, and returns the new length.</summary>
    private long Append(string id, string staged, long skip)
    {
        using var target = new FileStream(Part(id), FileMode.Append, FileAccess.Write, FileShare.None);
        using var incoming = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read);
        incoming.Position = skip;
        incoming.CopyTo(target);
        target.Flush(flushToDisk: true);
        return target.Length;
    }

    private async Task<bool> MatchesAsync(string id, string expected, CancellationToken cancellationToken)
    {
        await using var staged = new FileStream(Part(id), FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = await SHA256.HashDataAsync(staged, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(actual) == expected;
    }

    // ---- state -----------------------------------------------------------------------

    private sealed record StagedUpload(string UploadId, long Received, long? TotalBytes, string? Sha256, string? Fault, DateTimeOffset Updated);

    private static UploadState Describe(StagedUpload state) =>
        new(
            state.UploadId,
            state.Received,
            state.TotalBytes,
            state.TotalBytes is { } total && state.Received >= total && state.Fault is null,
            state.Sha256,
            state.Fault);

    private StagedUpload? Read(string id)
    {
        var path = State(id);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<StagedUpload>(File.ReadAllText(path), StateJson);
        }
        catch (JsonException)
        {
            // A state file a crashed process left half-written is not a reason to
            // refuse the upload: the staged bytes are still there, so the
            // upload restarts from zero rather than vanishing.
            return new StagedUpload(id, 0, null, null, null, _clock.GetUtcNow());
        }
    }

    private async Task WriteAsync(string id, StagedUpload state, CancellationToken cancellationToken)
    {
        // The staged file is created with the state, so its length is always
        // the length the state claims.
        if (!File.Exists(Part(id)))
        {
            await File.WriteAllBytesAsync(Part(id), [], cancellationToken).ConfigureAwait(false);
        }

        var json = JsonSerializer.Serialize(state, StateJson);
        await File.WriteAllTextAsync(State(id), json, cancellationToken).ConfigureAwait(false);
    }

    private long Length(string id) => File.Exists(Part(id)) ? new FileInfo(Part(id)).Length : 0;

    /// <summary>
    /// The recorded length, raised to what the staged file actually holds: an
    /// append interrupted after the bytes were written must not be replayed
    /// over them.
    /// </summary>
    private StagedUpload Live(StagedUpload state) =>
        Length(state.UploadId) > state.Received ? state with { Received = Length(state.UploadId) } : state;

    /// <summary>
    /// Drops staged uploads older than the configured age, so an abandoned
    /// upload is not a directory that grows for ever.
    /// </summary>
    private void Prune()
    {
        var cutoff = _clock.GetUtcNow() - TimeSpan.FromHours(Math.Max(1, Options.MaxAgeHours));
        foreach (var path in Directory.EnumerateFiles(Options.ResolvePath(), $"*{StateSuffix}"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (Read(id) is { Updated: var updated } && updated > cutoff)
            {
                continue;
            }

            Delete(id);
        }
    }

    private void Delete(string id)
    {
        foreach (var path in new[] { Part(id), State(id), Scratch(id) })
        {
            Remove(path);
        }
    }

    private static void Remove(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string Part(string id) => Path.Combine(Options.ResolvePath(), id + PartSuffix);

    private string State(string id) => Path.Combine(Options.ResolvePath(), id + StateSuffix);

    private string Scratch(string id) => Path.Combine(Options.ResolvePath(), id + ".incoming");

    // ---- argument checking ------------------------------------------------------------

    private static string NewId() => Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture);

    private static string RequireSafeId(string uploadId) =>
        !string.IsNullOrWhiteSpace(uploadId) && IsSafeId(uploadId)
            ? uploadId
            : throw SpatialException.BadArguments(
                "An upload id may only contain letters, digits, '-' and '_', and be at most 64 characters.");

    private static long? RequireTotal(long? totalBytes) =>
        totalBytes is { } total && total < 0
            ? throw SpatialException.BadArguments($"A declared upload size must not be negative, got {total}.")
            : totalBytes;

    private static string? RequireDigest(string? sha256) =>
        sha256 is { } digest && (digest.Length != 64 || !digest.All(Uri.IsHexDigit))
            ? throw SpatialException.BadArguments("A 'sha256' must be 64 hexadecimal characters.")
            : sha256?.ToLowerInvariant();

    private static T? RequireSame<T>(T? staged, T? declared, string what)
        where T : class =>
        staged is not null && declared is not null && !EqualityComparer<T>.Default.Equals(staged, declared)
            ? throw SpatialException.BadArguments(
                $"The upload already declares a different {what}; discard it and stage the document again.")
            : declared ?? staged;

    private static long? RequireSame(long? staged, long? declared, string what) =>
        staged is not null && declared is not null && staged != declared
            ? throw SpatialException.BadArguments($"The upload already declares a different {what}; discard it and stage the document again.")
            : declared ?? staged;

    private static SpatialException Missing(string id) =>
        SpatialException.Missing($"No staged upload '{id}'; it was never started, was ingested, or has expired.");
}
