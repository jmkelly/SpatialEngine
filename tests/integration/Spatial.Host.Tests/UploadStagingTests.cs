using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Host.Api;

namespace Spatial.Host.Tests;

/// <summary>
/// The file-backed upload staging behind <c>/api/uploads</c> (ADR-0083): the
/// append-only, resumable staging a large upload lands in before it is
/// ingested. The contract under test is deliberately narrow — an offset, a
/// declared total, a digest — because a resumable upload that cannot say how
/// far it got is a restart in disguise.
/// </summary>
public sealed class UploadStagingTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-uploads-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private FileUploadStaging Staging(long maxBytes = 1_000_000, int maxAgeHours = 24, TimeProvider? clock = null) =>
        new(new UploadOptions { Path = _directory, MaxAgeHours = maxAgeHours }, maxBytes, clock);

    private static MemoryStream Chunk(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task A_chunked_upload_stages_its_bytes_and_reports_the_offset_to_resume_from()
    {
        var staging = Staging();

        var started = await staging.StartAsync("big", 6, null);
        var first = await staging.AppendAsync("big", Chunk("abc"), new UploadAppend(0, 6));
        var second = await staging.AppendAsync("big", Chunk("def"), new UploadAppend(3, 6));

        Assert.Equal(0, started.Received);
        Assert.Equal(3, first.Received);
        Assert.False(first.Complete);
        Assert.Equal(6, second.Received);
        Assert.True(second.Complete);
        Assert.Null(second.Fault);

        using var staged = await staging.OpenAsync("big");
        using var reader = new StreamReader(staged);
        Assert.Equal("abcdef", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task A_resumed_upload_appends_from_the_offset_the_staging_reported()
    {
        var staging = Staging();
        await staging.StartAsync("resume", 6, null);
        await staging.AppendAsync("resume", Chunk("abc"), new UploadAppend(0, 6));

        // The client lost its response and asks what it had already sent.
        var state = await staging.DescribeAsync("resume");

        Assert.Equal(3, state.Received);
        var finished = await staging.AppendAsync(
            "resume", Chunk("def"), new UploadAppend(state.Received, 6));
        Assert.True(finished.Complete);
    }

    [Fact]
    public async Task An_overlapping_append_is_accepted_when_the_bytes_already_staged_match()
    {
        var staging = Staging();
        await staging.StartAsync("overlap", 6, null);
        await staging.AppendAsync("overlap", Chunk("abc"), new UploadAppend(0, 6));

        // The acknowledgement of the first chunk was lost, so the client
        // resends it. The already-staged prefix matches, so nothing changes.
        var resent = await staging.AppendAsync("overlap", Chunk("abc"), new UploadAppend(0, 6));

        Assert.Equal(3, resent.Received);
        Assert.False(resent.Complete);
    }

    [Fact]
    public async Task An_overlapping_append_that_differs_is_rejected_and_changes_nothing()
    {
        var staging = Staging();
        await staging.StartAsync("clash", 6, null);
        await staging.AppendAsync("clash", Chunk("abc"), new UploadAppend(0, 6));

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            staging.AppendAsync("clash", Chunk("xyz"), new UploadAppend(0, 6)));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Equal(3, (await staging.DescribeAsync("clash")).Received);
    }

    [Fact]
    public async Task An_append_beyond_the_staged_bytes_is_rejected_naming_the_offset_to_resume_from()
    {
        var staging = Staging();
        await staging.StartAsync("gap", 9, null);
        await staging.AppendAsync("gap", Chunk("abc"), new UploadAppend(0, 9));

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            staging.AppendAsync("gap", Chunk("defghi"), new UploadAppend(6, 9)));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("3", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_upload_cannot_grow_past_its_declared_total()
    {
        var staging = Staging();
        await staging.StartAsync("over", 3, null);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            staging.AppendAsync("over", Chunk("abcd"), new UploadAppend(0, 3)));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Equal(0, (await staging.DescribeAsync("over")).Received);
    }

    [Fact]
    public async Task Staged_bytes_are_bounded_by_the_ingest_byte_cap()
    {
        var staging = Staging(maxBytes: 4);
        await staging.StartAsync("capped", null, null);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            staging.AppendAsync("capped", Chunk("abcde"), new UploadAppend(0)));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Equal(0, (await staging.DescribeAsync("capped")).Received);
    }

    [Fact]
    public async Task A_declared_digest_is_verified_when_the_last_byte_arrives()
    {
        var staging = Staging();
        await staging.StartAsync("digest", 6, Sha256("abcdef"));
        await staging.AppendAsync("digest", Chunk("abc"), new UploadAppend(0, 6));

        var completed = await staging.AppendAsync(
            "digest", Chunk("def"), new UploadAppend(3, 6));

        Assert.True(completed.Complete);
    }

    [Fact]
    public async Task A_digest_that_does_not_match_leaves_the_upload_incomplete_and_says_so()
    {
        var staging = Staging();
        await staging.StartAsync("wrong", 6, Sha256("abcdef"));
        await staging.AppendAsync("wrong", Chunk("abc"), new UploadAppend(0, 6));

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            staging.AppendAsync("wrong", Chunk("dEf"), new UploadAppend(3, 6)));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        var state = await staging.DescribeAsync("wrong");
        Assert.False(state.Complete);
        Assert.NotNull(state.Fault);
    }

    [Fact]
    public async Task An_upload_id_that_is_not_a_safe_token_is_rejected()
    {
        var staging = Staging();

        await Assert.ThrowsAsync<SpatialException>(() =>
            staging.StartAsync("../escape", null, null));
        await Assert.ThrowsAsync<SpatialException>(() =>
            staging.StartAsync("has space", null, null));
    }

    [Fact]
    public async Task A_repeated_start_returns_the_existing_upload_so_a_lost_response_is_recoverable()
    {
        var staging = Staging();
        await staging.StartAsync("again", 6, null);
        await staging.AppendAsync("again", Chunk("abc"), new UploadAppend(0, 6));

        var repeated = await staging.StartAsync("again", 6, null);
        Assert.Equal(3, repeated.Received);

        var conflicting = await Assert.ThrowsAsync<SpatialException>(() =>
            staging.StartAsync("again", 9, null));
        Assert.Equal(SpatialException.InvalidArguments, conflicting.Code);
    }

    [Fact]
    public async Task An_unknown_upload_is_not_found()
    {
        var staging = Staging();

        Assert.Equal(
            SpatialException.NotFound,
            (await Assert.ThrowsAsync<SpatialException>(() => staging.DescribeAsync("ghost"))).Code);
        Assert.Equal(
            SpatialException.NotFound,
            (await Assert.ThrowsAsync<SpatialException>(() => staging.OpenAsync("ghost"))).Code);
    }

    [Fact]
    public async Task A_discarded_upload_is_gone()
    {
        var staging = Staging();
        await staging.StartAsync("drop", 6, null);
        await staging.AppendAsync("drop", Chunk("abc"), new UploadAppend(0, 6));

        Assert.True(await staging.DiscardAsync("drop"));
        Assert.False(await staging.DiscardAsync("drop"));
        Assert.Empty(await staging.ListAsync());
    }

    [Fact]
    public async Task A_staged_upload_older_than_the_maximum_age_is_pruned()
    {
        var clock = new MovableClock(new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero));
        var staging = Staging(maxAgeHours: 1, clock: clock);
        await staging.StartAsync("stale", 6, null);
        await staging.AppendAsync("stale", Chunk("abc"), new UploadAppend(0, 6));

        clock.Advance(TimeSpan.FromHours(2));

        Assert.Empty(await staging.ListAsync());
        Assert.Equal(
            SpatialException.NotFound,
            (await Assert.ThrowsAsync<SpatialException>(() => staging.DescribeAsync("stale"))).Code);
    }

    [Fact]
    public async Task A_cancelled_append_leaves_the_staged_upload_exactly_as_it_was()
    {
        var staging = Staging();
        await staging.StartAsync("cancel", 6, null);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            staging.AppendAsync("cancel", Chunk("abc"), new UploadAppend(0, 6), cancellation.Token));

        Assert.Equal(0, (await staging.DescribeAsync("cancel")).Received);
    }

    [Fact]
    public async Task An_append_cancelled_mid_chunk_stages_no_part_of_it_and_resumes_from_the_same_offset()
    {
        // A pre-cancelled token proves nothing about durability: the request dies
        // before a byte is read. This one delivers half a chunk and is cancelled
        // while the rest is still in flight, which is the case ADR-0083 §3
        // claims is safe — the staged length is the file's length, so half a
        // chunk never becomes half an upload.
        var staging = Staging();
        await staging.StartAsync("midflight", 6, null);
        await staging.AppendAsync("midflight", Chunk("abc"), new UploadAppend(0, 6));

        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            staging.AppendAsync("midflight", new InterruptingStream(Encoding.UTF8.GetBytes("def"), cancellation), new UploadAppend(3, 6), cancellation.Token));

        // The interrupted half must not be counted: the document is still the
        // three bytes that were acknowledged, and the client resumes from there.
        Assert.Equal(3, (await staging.DescribeAsync("midflight")).Received);

        var resumed = await staging.AppendAsync("midflight", Chunk("def"), new UploadAppend(3, 6));
        Assert.Equal(6, resumed.Received);
        Assert.True(resumed.Complete);
    }

    [Fact]
    public async Task Bytes_left_staged_by_an_interrupted_append_are_reported_rather_than_replayed_over()
    {
        // The other half of the ADR-0083 §3 claim. If an append is cut after
        // its bytes reached the staged file but before the state was written,
        // the next append must not be told its offset is ahead of the staging —
        // that is the difference between a resume and a restart.
        var staging = Staging();
        await staging.StartAsync("torn", 6, null);
        await staging.AppendAsync("torn", Chunk("abc"), new UploadAppend(0, 6));

        // Stand in for the interrupted write: the bytes are on disk, the state
        // file never learned about them.
        await File.AppendAllTextAsync(Path.Combine(_directory, "torn.part"), "d");

        Assert.Equal(4, (await staging.DescribeAsync("torn")).Received);

        // Re-sending from the offset the client believes in is an overlap, not a
        // gap, and is accepted because the bytes it repeats do match.
        var resumed = await staging.AppendAsync("torn", Chunk("def"), new UploadAppend(3, 6));
        Assert.Equal(6, resumed.Received);
        Assert.True(resumed.Complete);
    }

    [Fact]
    public async Task An_overlapping_chunk_cannot_smuggle_past_the_declared_total()
    {
        // Accepting a chunk addressed below the staged length widens what the
        // staging will read, so the cap has to hold on the offset the chunk was
        // addressed at and not merely on the overlap it happens to repeat.
        var staging = Staging();
        await staging.StartAsync("smuggle", 6, null);
        await staging.AppendAsync("smuggle", Chunk("abc"), new UploadAppend(0, 6));

        // "abcdef" addressed at 0 repeats the three staged bytes and adds three,
        // which is exactly the declared document and must be accepted.
        Assert.Equal(6, (await staging.AppendAsync("smuggle", Chunk("abcdef"), new UploadAppend(0, 6))).Received);

        // Anything longer is not, even though most of it is re-sent bytes.
        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            staging.AppendAsync("smuggle", Chunk("abcdefghij"), new UploadAppend(0, 6)));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Equal(6, (await staging.DescribeAsync("smuggle")).Received);
    }

    /// <summary>
    /// A chunk that hands over part of itself and is then cancelled, which is
    /// what a dropped connection looks like from inside the staging.
    /// </summary>
    private sealed class InterruptingStream(byte[] bytes, CancellationTokenSource cancellation) : Stream
    {
        private int _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;

        public override long Position
        {
            get => _served;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_served >= 2)
            {
                // The rest of the chunk never arrives; the request is cancelled
                // while the body is still being read.
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }

            buffer[offset] = bytes[_served++];
            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A clock the tests move by hand, so expiry needs no sleeping.</summary>
    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
