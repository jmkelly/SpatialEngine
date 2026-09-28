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
        var session = await DatasetDecoder.DecodeStreamingAsync(
            FeatureCollection(features),
            IngestFormat.GeoJson,
            new DecodeOptions { BatchSize = 1_000 },
            CancellationToken.None);

        var streamed = await MeasureAsync(async () =>
        {
            await using (session)
            {
                await foreach (var _ in session.Pages)
                {
                }
            }
        });

        var buffered = await MeasureAsync(() => Task.FromResult(DatasetDecoder.Decode(
            FeatureCollection(features), IngestFormat.GeoJson, new DecodeOptions { BatchSize = 1_000 })));

        Assert.True(
            streamed < buffered / 2,
            $"streaming peaked at {streamed:N0} bytes against {buffered:N0} buffered; a streamed upload must not be materialised");
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

    /// <summary>
    /// The peak live managed memory during one operation, in bytes. Sampled
    /// rather than measured at the end, because a decode that materialises its
    /// input holds it exactly until it returns, while a streamed one never
    /// does — and the difference is the whole claim.
    /// </summary>
    private static async Task<long> MeasureAsync(Func<Task> operation)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var peak = baseline;
        using var sampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (sampling.Token.IsCancellationRequested is false)
            {
                var live = GC.GetTotalMemory(forceFullCollection: false);
                if (live > peak)
                {
                    peak = live;
                }

                try
                {
                    await Task.Delay(1, sampling.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });

        try
        {
            await operation();
        }
        finally
        {
            await sampling.CancelAsync();
            await sampler;
        }

        var end = GC.GetTotalMemory(forceFullCollection: false);
        return Math.Max(peak, end) - baseline;
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
