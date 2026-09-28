using System.Text;
using Spatial.Core.Geometry;

namespace Spatial.Ingest.Codec.Tests;

/// <summary>
/// The declared-source-CRS half of ADR-0041 §4: a format that declares a CRS
/// gets it honoured. Before this, <c>DecodeOptions.Srid</c> was stamped on
/// every geometry regardless of what the document said, so a GeoJSON carrying
/// <c>"crs": …EPSG::3857</c> decoded to OSGB-metre coordinates labelled
/// EPSG:4326 and loaded without an error.
/// </summary>
public sealed class DeclaredCrsTests
{
    private const string WebMercator = "urn:ogc:def:crs:EPSG::3857";
    private const string Osgb = "urn:ogc:def:crs:EPSG::27700";

    /// <summary>Records the reprojection calls a decode made.</summary>
    private sealed class RecordingReprojection : IIngestReprojection
    {
        public List<(string Source, string Target)> Calls { get; } = [];

        public IGeometry Reproject(IGeometry geometry, string source, string target, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((source, target));
            return GeometryFactory.CreatePoint(0, 0, CoordinateReference.Epsg(3857));
        }
    }

    private static DecodedDataset Decode(string json, DecodeOptions options) =>
        DatasetDecoder.Decode(new MemoryStream(Encoding.UTF8.GetBytes(json)), IngestFormat.GeoJson, options);

    private static string CollectionWithCrs(string crs) =>
        "{\"type\":\"FeatureCollection\",\"crs\":{\"type\":\"name\",\"properties\":{\"name\":\"" + crs
        + "\"}},\"features\":[{\"type\":\"Feature\",\"properties\":{},\"geometry\":{\"type\":\"Point\",\"coordinates\":[1,2]}}]}";

    [Fact]
    public void A_declared_crs_the_decode_cannot_transform_is_a_failure_not_a_silent_stamp()
    {
        var failure = Assert.Throws<IngestFormatException>(
            () => Decode(CollectionWithCrs(WebMercator), new DecodeOptions { Srid = 4326 }));

        Assert.Contains("3857", failure.Message, StringComparison.Ordinal);
        Assert.Contains("4326", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_declared_crs_is_reprojected_into_the_target_crs()
    {
        var reprojection = new RecordingReprojection();

        var decoded = Decode(
            CollectionWithCrs(WebMercator),
            new DecodeOptions { Srid = 27700, Reprojector = reprojection });

        Assert.Equal([("EPSG:3857", "EPSG:27700")], reprojection.Calls);
        var point = Assert.IsType<Point>(decoded.Pages[0][0]["geometry"].GeometryValue);
        Assert.Equal(CoordinateReference.Epsg(3857), point.CoordinateReference);
    }

    [Fact]
    public void A_declared_crs_equal_to_the_target_needs_no_reprojection()
    {
        var reprojection = new RecordingReprojection();

        var decoded = Decode(
            CollectionWithCrs("urn:ogc:def:crs:EPSG::4326"),
            new DecodeOptions { Srid = 4326, Reprojector = reprojection });

        Assert.Empty(reprojection.Calls);
        Assert.False(decoded.Report.Crs.Reprojected);
    }

    [Fact]
    public void A_declared_crs_conflicting_with_the_asserted_source_is_a_failure()
    {
        var failure = Assert.Throws<IngestFormatException>(() => Decode(
            CollectionWithCrs(Osgb),
            new DecodeOptions { Srid = 4326, SourceSrid = 4326, Reprojector = new RecordingReprojection() }));

        Assert.Contains("27700", failure.Message, StringComparison.Ordinal);
        Assert.Contains("4326", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_asserted_source_crs_matching_the_declaration_is_honoured()
    {
        var reprojection = new RecordingReprojection();

        var decoded = Decode(
            CollectionWithCrs(WebMercator),
            new DecodeOptions { Srid = 27700, SourceSrid = 3857, Reprojector = reprojection });

        Assert.Equal([("EPSG:3857", "EPSG:27700")], reprojection.Calls);
        Assert.Equal(3857, decoded.Report.Crs.SourceSrid);
        Assert.Equal(WebMercator, decoded.Report.Crs.Declared);
    }

    [Fact]
    public void The_opengis_http_and_short_urn_spellings_resolve_to_the_same_code()
    {
        var reprojection = new RecordingReprojection();

        Decode(
            CollectionWithCrs("http://www.opengis.net/def/crs/EPSG/0/3857"),
            new DecodeOptions { Srid = 4326, Reprojector = reprojection });

        Assert.Equal([("EPSG:3857", "EPSG:4326")], reprojection.Calls);
    }

    [Fact]
    public void An_unresolvable_crs_name_is_a_failure()
    {
        var failure = Assert.Throws<IngestFormatException>(
            () => Decode(CollectionWithCrs("urn:ogc:def:crs:NOPE::1"), new DecodeOptions { Srid = 4326 }));

        Assert.Contains("NOPE", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Newline_delimited_geojson_reads_a_crs_from_its_first_record()
    {
        var reprojection = new RecordingReprojection();
        var body = string.Join("\n", NdRecord(WebMercator, true), NdRecord(null, true));

        var decoded = DatasetDecoder.Decode(
            new MemoryStream(Encoding.UTF8.GetBytes(body)),
            IngestFormat.NewlineDelimitedGeoJson,
            new DecodeOptions { Srid = 4326, Reprojector = reprojection });

        Assert.Equal(2, reprojection.Calls.Count);
        Assert.All(reprojection.Calls, call => Assert.Equal(("EPSG:3857", "EPSG:4326"), call));
        Assert.Equal(WebMercator, decoded.Report.Crs.Declared);
    }

    [Fact]
    public void Newline_delimited_geojson_rejects_a_later_record_declaring_a_different_crs()
    {
        var body = string.Join("\n", NdRecord(WebMercator), NdRecord(Osgb));

        var failure = Assert.Throws<IngestFormatException>(() => DatasetDecoder.Decode(
            new MemoryStream(Encoding.UTF8.GetBytes(body)),
            IngestFormat.NewlineDelimitedGeoJson,
            new DecodeOptions { Srid = 4326, Reprojector = new RecordingReprojection() }));

        Assert.Contains("line 2", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Csv_reads_a_crs_from_a_leading_comment_directive()
    {
        var reprojection = new RecordingReprojection();
        var body = $"# crs={WebMercator}\nlon,lat\n1,2\n3,4\n";

        var decoded = DatasetDecoder.Decode(
            new MemoryStream(Encoding.UTF8.GetBytes(body)),
            IngestFormat.Csv,
            new DecodeOptions { Srid = 4326, Reprojector = reprojection });

        Assert.Equal(2, reprojection.Calls.Count);
        Assert.All(reprojection.Calls, call => Assert.Equal(("EPSG:3857", "EPSG:4326"), call));
        Assert.Equal(WebMercator, decoded.Report.Crs.Declared);
    }

    /// <summary>One ND-GeoJSON record, optionally declaring a CRS.</summary>
    private static string NdRecord(string? crs, bool point = false) =>
        "{\"type\":\"Feature\"" + (crs is null ? string.Empty : ",\"crs\":{\"type\":\"name\",\"properties\":{\"name\":\"" + crs + "\"}}")
        + ",\"properties\":{},\"geometry\":" + (point ? "{\"type\":\"Point\",\"coordinates\":[1,2]}" : "null") + "}";

    [Fact]
    public async Task A_reprojection_that_is_cancelled_throws_rather_than_emitting_a_page()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var session = await DatasetDecoder.DecodeStreamingAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(CollectionWithCrs(WebMercator))),
                IngestFormat.GeoJson,
                new DecodeOptions { Srid = 4326, Reprojector = new RecordingReprojection() },
                cancellation.Token);
            await using (session)
            {
                await foreach (var page in session.Pages.WithCancellation(cancellation.Token))
                {
                    _ = page;
                }
            }
        });
    }
}
