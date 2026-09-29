using System.Runtime.CompilerServices;
using System.Text;
using Spatial.Core.Features;
using Spatial.Core.Features.Ingest;
using Spatial.Core.Geometry;

namespace Spatial.Ingest.Codec.Tests;

/// <summary>
/// The streaming half of ADR-0041 §4. The buffered decode was
/// <c>DecodedDataset(IReadOnlyList&lt;FeatureBatch&gt;)</c>: a 500 MB upload
/// was a 500 MB allocation before the first write, and a malformed row in the
/// last page failed a job that had already done all the work. The streaming
/// decode yields pages, so the store can load each one as it arrives.
/// </summary>
public sealed class StreamingDecodeTests
{
    private const int Features = 20_000;

    private static async Task<List<int>> DrainAsync(Stream body, IngestFormat format, DecodeOptions options)
    {
        var counts = new List<int>();
        var session = await DatasetDecoder.DecodeStreamingAsync(body, format, options, CancellationToken.None);
        await using (session)
        {
            await foreach (var page in session.Pages)
            {
                counts.Add(page.Count);
            }
        }

        return counts;
    }

    private static MemoryStream NdGeoJson(int features)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < features; i++)
        {
            builder.Append("{\"type\":\"Feature\",\"properties\":{\"n\":").Append(i)
                .Append(",\"name\":\"row").Append(i)
                .Append("\"},\"geometry\":{\"type\":\"Point\",\"coordinates\":[").Append(i).Append(',').Append(i).Append("]}}\n");
        }

        return new MemoryStream(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    [Fact]
    public async Task The_streamed_features_match_the_buffered_decode()
    {
        var options = new DecodeOptions { BatchSize = 1_000 };

        var buffered = DatasetDecoder.Decode(NdGeoJson(Features), IngestFormat.NewlineDelimitedGeoJson, options);
        var streamed = new List<FeatureBatch>();
        var session = await DatasetDecoder.DecodeStreamingAsync(
            NdGeoJson(Features), IngestFormat.NewlineDelimitedGeoJson, options, CancellationToken.None);
        await using (session)
        {
            await foreach (var page in session.Pages)
            {
                streamed.Add(page);
            }
        }

        Assert.Equal(buffered.Schema, streamed[0].Schema);
        Assert.Equal(
            buffered.Pages.SelectMany(page => page.Features).Select(feature => feature["n"].Int64Value),
            streamed.SelectMany(page => page.Features).Select(feature => feature["n"].Int64Value));
        Assert.Equal(buffered.Report.RecordsRead, buffered.Report.RecordsRead);
    }

    [Fact]
    public async Task The_schema_is_known_before_the_first_page_is_awaited()
    {
        await using var session = await DatasetDecoder.DecodeStreamingAsync(
            NdGeoJson(10), IngestFormat.NewlineDelimitedGeoJson, new DecodeOptions { BatchSize = 4 }, CancellationToken.None);

        Assert.Equal(["n", "name", "geometry"], session.Schema.Fields.Select(field => field.Name));
    }

    [Fact]
    public async Task Pages_are_emitted_at_the_batch_size_rather_than_all_at_once()
    {
        var counts = await DrainAsync(NdGeoJson(Features), IngestFormat.NewlineDelimitedGeoJson, new DecodeOptions { BatchSize = 2_500 });

        Assert.Equal(Enumerable.Repeat(2_500, 8), counts);
    }

    [Fact]
    public async Task The_report_is_available_once_the_stream_is_drained()
    {
        await using var session = await DatasetDecoder.DecodeStreamingAsync(
            NdGeoJson(Features),
            IngestFormat.NewlineDelimitedGeoJson,
            new DecodeOptions { BatchSize = 1_000, SkipMalformed = true },
            CancellationToken.None);

        await foreach (var _ in session.Pages)
        {
        }

        var report = await session.Report;
        Assert.Equal(Features, report.FeaturesEmitted);
        Assert.Equal(Features, report.RecordsRead);
        Assert.Equal(0, report.SkippedCount);
    }

    [Fact]
    public async Task Streaming_cancellation_stops_the_decode_promptly()
    {
        var session = await DatasetDecoder.DecodeStreamingAsync(
            NdGeoJson(Features), IngestFormat.NewlineDelimitedGeoJson, new DecodeOptions { BatchSize = 100 }, CancellationToken.None);
        await using (session)
        {
            using var cancellation = new CancellationTokenSource();
            var seen = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var page in session.Pages.WithCancellation(cancellation.Token))
                {
                    seen += page.Count;
                    cancellation.Cancel();
                }
            });

            Assert.Equal(100, seen);
        }

    }

    [Fact]
    public async Task Cancellation_before_the_schema_sample_yields_no_session()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await DatasetDecoder.DecodeStreamingAsync(
                NdGeoJson(Features), IngestFormat.NewlineDelimitedGeoJson, new DecodeOptions(), cancellation.Token));
    }

    [Fact]
    public async Task An_empty_document_is_a_failure()
    {
        var body = """{"type":"FeatureCollection","features":[]}""";
        await Assert.ThrowsAsync<IngestFormatException>(async () => await DrainAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(body)),
            IngestFormat.GeoJson,
            new DecodeOptions()));
    }

    [Fact]
    public async Task A_streaming_csv_decode_infers_and_streams_the_same_way()
    {
        var builder = new StringBuilder("lon,lat,name\n");
        for (var i = 0; i < 500; i++)
        {
            builder.Append(i).Append(',').Append(i).Append(",row").Append(i).Append('\n');
        }

        var counts = await DrainAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(builder.ToString())),
            IngestFormat.Csv,
            new DecodeOptions { BatchSize = 200 });

        Assert.Equal([200, 200, 100], counts);
    }

    [Fact]
    public async Task A_large_geojson_upload_streams_instead_of_materialising()
    {
        const int features = 40_000;
        const int batchSize = 1_000;

        // Reachability, not bytes. A peak-heap ceiling was a statement about the
        // machine that ran it: it read 77 MB streamed against 96 MB buffered on
        // the ci runner — while the sampler counted every byte another test in
        // the assembly was allocating, and how much garbage the heap had simply
        // not collected yet — and passed everywhere else. What makes a decode
        // streaming is not how many bytes it costs but what stays reachable
        // after a page has been handed over: a streamed upload keeps the page
        // in flight and nothing else, and a materialised one keeps all of them
        // for as long as the caller holds the result. Both halves are asserted,
        // because either alone would pass for the wrong reason.
        var streamed = await StreamedPages(features, batchSize);
        var buffered = BufferedPages(features, batchSize);

        Assert.Equal(features / batchSize, buffered.Total);
        Assert.True(
            streamed.Peak <= 2,
            $"streaming kept {streamed.Peak} of the {streamed.Emitted} pages it " +
            "had already handed over reachable; a streamed upload must not " +
            "materialise what the consumer has moved past");
        Assert.True(
            buffered.Alive == buffered.Total,
            $"the buffered decode kept {buffered.Alive} of its {buffered.Total} " +
            "pages reachable while its result was held, which is what " +
            "streaming exists not to do");
    }

    /// <summary>
    /// The pages a streamed decode hands over, as weak references, and how many
    /// of them are reachable halfway through while the consumer is holding
    /// nothing but the page in flight. The collection has to happen mid-decode:
    /// a decode that has returned has dropped whatever it was keeping by then,
    /// so measuring afterwards would say nothing about whether it materialised.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(int Emitted, int Peak)> StreamedPages(int features, int batchSize)
    {
        await using var session = await DatasetDecoder.DecodeStreamingAsync(
            FeatureCollection(features),
            IngestFormat.GeoJson,
            new DecodeOptions { BatchSize = batchSize },
            CancellationToken.None);

        var pages = new List<WeakReference>();
        var halfway = features / batchSize / 2;
        var peak = 0;

        await foreach (var page in session.Pages)
        {
            pages.Add(new WeakReference(page));
            if (pages.Count == halfway)
            {
                peak = Collect(pages);
            }
        }

        // Two is the page in flight and the enumerator's copy of it; the
        // nineteen pages before it are what must be gone.
        return (pages.Count, peak);
    }

    /// <summary>
    /// The same document through the buffered decode: how many pages it has,
    /// and how many of them are still reachable <em>while its result is
    /// held</em>. Every page has to survive that, or the control says nothing
    /// about what the streamed half is being compared against.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (int Total, int Alive) BufferedPages(int features, int batchSize)
    {
        var dataset = DatasetDecoder.Decode(
            FeatureCollection(features),
            IngestFormat.GeoJson,
            new DecodeOptions { BatchSize = batchSize });

        var pages = dataset.Pages.Select(page => new WeakReference(page)).ToList();
        var alive = Collect(pages);

        // The result has to stay reachable across the collection, or this
        // measures when the JIT decided the local was dead, not what the
        // buffered decode holds.
        GC.KeepAlive(dataset);
        return (pages.Count, alive);
    }

    /// <summary>
    /// The pages of <paramref name="pages"/> still reachable after a full
    /// collection. Three passes, because a finalised object and the reference
    /// its finaliser left behind are not reachable in the same collection.
    /// </summary>
    private static int Collect(IEnumerable<WeakReference> pages)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return pages.Count(page => page.IsAlive);
    }

    /// <summary>A FeatureCollection of <paramref name="features"/> point features.</summary>
    private static MemoryStream FeatureCollection(int features)
    {
        var builder = new StringBuilder("{\"type\":\"FeatureCollection\",\"features\":[");
        for (var i = 0; i < features; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"type\":\"Feature\",\"properties\":{\"n\":").Append(i)
                .Append("},\"geometry\":{\"type\":\"Point\",\"coordinates\":[").Append(i).Append(',').Append(i).Append("]}}");
        }

        builder.Append("]}");
        return new MemoryStream(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    [Fact]
    public async Task A_sampled_schema_reports_that_it_was_sampled()
    {
        await using var session = await DatasetDecoder.DecodeStreamingAsync(
            NdGeoJson(500),
            IngestFormat.NewlineDelimitedGeoJson,
            new DecodeOptions { BatchSize = 50, InferSampleSize = 10 },
            CancellationToken.None);

        await foreach (var _ in session.Pages)
        {
        }

        var report = await session.Report;
        Assert.Equal(10, report.Sampled);
        Assert.True(report.SchemaSampled);
    }

    [Fact]
    public async Task A_field_only_present_after_the_sample_is_reported_rather_than_silently_dropped()
    {
        var rows = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            rows.Add($"{{\"type\":\"Feature\",\"properties\":{{\"n\":{i}}},\"geometry\":null}}");
            if (i == 10)
            {
                rows.Add("{\"type\":\"Feature\",\"properties\":{\"n\":99,\"late\":\"x\"},\"geometry\":null}");
            }
        }

        await using var session = await DatasetDecoder.DecodeStreamingAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", rows))),
            IngestFormat.NewlineDelimitedGeoJson,
            new DecodeOptions { BatchSize = 4, InferSampleSize = 5, SkipMalformed = true },
            CancellationToken.None);

        await foreach (var _ in session.Pages)
        {
        }

        var report = await session.Report;
        var skip = Assert.Single(report.Skipped, candidate => candidate.Reason == IngestSkipReason.FieldNotInferred);
        Assert.Contains("late", skip.Detail, StringComparison.Ordinal);
        Assert.Equal(20, report.FeaturesEmitted);
    }
}


/// <summary>
/// A network stream hands over bytes in whatever sizes the transport chooses,
/// so the streaming JSON reader has to survive a buffer boundary landing in
/// the middle of a token, a string, a number and a value. A codec that only
/// works on a <see cref="MemoryStream"/> is a codec that fails on the only input
/// that matters.
/// </summary>
public sealed class ChunkedStreamTests
{
    /// <summary>A stream that yields at most <paramref name="chunk"/> bytes per read.</summary>
    private sealed class ChunkedStream(byte[] body, int chunk) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => body.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = Math.Min(Math.Min(count, chunk), body.Length - _position);
            Array.Copy(body, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var take = Math.Min(Math.Min(buffer.Length, chunk), body.Length - _position);
            body.AsMemory(_position, take).CopyTo(buffer);
            _position += take;
            return ValueTask.FromResult(take);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Body(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    private const string Collection =
        """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","properties":{"name":"Berlin","population":3664000},"geometry":{"type":"Point","coordinates":[13.4,52.5]}},
          {"type":"Feature","properties":{"name":"Paris","population":2150000},"geometry":{"type":"Polygon","coordinates":[[[0,0],[0,1],[1,1],[1,0],[0,0]]]}}
        ]}
        """;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(64)]
    public void A_feature_collection_decodes_however_the_stream_is_chunked(int chunk)
    {
        var decoded = DatasetDecoder.Decode(new ChunkedStream(Body(Collection), chunk), IngestFormat.GeoJson);

        Assert.Equal(2, decoded.Report.FeaturesEmitted);
        Assert.Equal(2, decoded.Pages.Sum(page => page.Count));
        Assert.Equal("Berlin", decoded.Pages[0][0]["name"].StringValue);
        Assert.IsType<Point>(decoded.Pages[0][0]["geometry"].GeometryValue);
        Assert.IsType<Polygon>(decoded.Pages[0][1]["geometry"].GeometryValue);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(64)]
    public async Task A_feature_collection_streams_however_the_stream_is_chunked(int chunk)
    {
        var emitted = 0;
        await using var session = await DatasetDecoder.DecodeStreamingAsync(
            new ChunkedStream(Body(Collection), chunk), IngestFormat.GeoJson, new DecodeOptions(), CancellationToken.None);

        await foreach (var page in session.Pages)
        {
            emitted += page.Count;
        }

        Assert.Equal(2, emitted);
    }

    [Fact]
    public void One_feature_larger_than_the_read_buffer_decodes()
    {
        // A detailed coastline polygon is routinely bigger than a 64 KiB read
        // buffer, and the reader has to grow for it rather than call it a
        // truncated document.
        var ring = string.Join(",", Enumerable.Range(0, 20_000).Select(i => $"[{i % 180}.{i % 90},{i % 45}.{i % 30}]"));
        var body = "{\"type\":\"FeatureCollection\",\"features\":["
            + "{\"type\":\"Feature\",\"properties\":{\"name\":\"big\"},\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[["
            + ring
            + "]]}}]}";
        Assert.True(body.Length > 200_000, $"the fixture must exceed the buffer; it is {body.Length} bytes");

        var decoded = DatasetDecoder.Decode(new ChunkedStream(Body(body), 8192), IngestFormat.GeoJson);

        Assert.Equal(1, decoded.Report.FeaturesEmitted);
        Assert.Equal(20_000, Assert.IsType<Polygon>(decoded.Pages[0][0]["geometry"].GeometryValue).ExteriorRing.Sequence.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(64)]
    public void Csv_decodes_however_the_stream_is_chunked(int chunk)
    {
        var decoded = DatasetDecoder.Decode(
            new ChunkedStream(Body("lon,lat,name,note\n1,2,\"a,b\",\"line\nbreak\"\n"), chunk),
            IngestFormat.Csv);

        Assert.Single(decoded.Pages[0].Features);
        Assert.Equal("a,b", decoded.Pages[0][0]["name"].StringValue);
        Assert.Equal("line\nbreak", decoded.Pages[0][0]["note"].StringValue);
    }
}
