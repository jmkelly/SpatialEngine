using System.Text;
using Spatial.Core.Features;
using Spatial.Core.Features.Ingest;

namespace Spatial.Ingest.Codec.Tests;

/// <summary>
/// The decode-report half of ADR-0041 §4. The reference documentation already
/// said malformed features are dropped, but nothing observable said so: the
/// decode returned pages and the caller could not tell a clean load from one
/// that quietly lost rows.
/// </summary>
public sealed class DecodeReportTests
{
    private static DecodedDataset Decode(string body, IngestFormat format, DecodeOptions? options = null) =>
        DatasetDecoder.Decode(new MemoryStream(Encoding.UTF8.GetBytes(body)), format, options);

    [Fact]
    public void A_clean_decode_reports_what_it_read_and_infers_each_field_with_evidence()
    {
        var body = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","properties":{"name":"A","count":2},"geometry":null},
              {"type":"Feature","properties":{"name":"B","count":3},"geometry":null}]}
            """;

        var decoded = Decode(body, IngestFormat.GeoJson);

        var report = decoded.Report;
        Assert.Equal(2, report.RecordsRead);
        Assert.Equal(2, report.FeaturesEmitted);
        Assert.Equal(0, report.SkippedCount);
        Assert.Empty(report.Skipped);
        Assert.False(report.SkipsTruncated);

        var count = report.Inferred.Single(field => field.Name == "count");
        Assert.Equal(AttributeKind.Int64, count.Kind);
        Assert.False(count.Nullable);
        Assert.Equal(0, count.Nulls);
        Assert.Equal([AttributeKind.Int64], count.Observed);

        var name = report.Inferred.Single(field => field.Name == "name");
        Assert.Equal([AttributeKind.String], name.Observed);
    }

    [Fact]
    public void A_null_only_field_is_reported_as_nullable_string_with_no_observed_kinds()
    {
        var body = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","properties":{"note":null},"geometry":null}]}
            """;

        var report = Decode(body, IngestFormat.GeoJson).Report;

        var note = report.Inferred.Single(field => field.Name == "note");
        Assert.Equal(AttributeKind.String, note.Kind);
        Assert.True(note.Nullable);
        Assert.Equal(1, note.Nulls);
        Assert.Empty(note.Observed);
    }

    [Fact]
    public void A_mixed_column_widens_and_the_report_says_which_kinds_were_seen()
    {
        var body = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","properties":{"v":1},"geometry":null},
              {"type":"Feature","properties":{"v":1.5},"geometry":null},
              {"type":"Feature","properties":{"v":null},"geometry":null},
              {"type":"Feature","properties":{"v":"x"},"geometry":null}]}
            """;

        var report = Decode(body, IngestFormat.GeoJson).Report;

        var value = report.Inferred.Single(field => field.Name == "v");
        Assert.Equal(AttributeKind.String, value.Kind);
        Assert.True(value.Nullable);
        Assert.Equal(1, value.Nulls);
        Assert.Equal([AttributeKind.Int64, AttributeKind.Double, AttributeKind.String], value.Observed);
    }

    [Fact]
    public void A_malformed_csv_row_is_still_a_failure_by_default()
    {
        var body = "lon,lat,name\n1,2,A\n3,x,B\n";

        var failure = Assert.Throws<IngestFormatException>(() => Decode(body, IngestFormat.Csv));

        Assert.Contains("row 3", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_malformed_csv_row_is_dropped_and_reported_when_the_decode_is_told_to_skip()
    {
        var body = "lon,lat,name\n1,2,A\n3,x,B\n5,6,C\n";

        var decoded = Decode(body, IngestFormat.Csv, new DecodeOptions { SkipMalformed = true });

        var report = decoded.Report;
        Assert.Equal(3, report.RecordsRead);
        Assert.Equal(2, report.FeaturesEmitted);
        Assert.Equal(1, report.SkippedCount);
        var skip = Assert.Single(report.Skipped);
        Assert.Equal(IngestSkipReason.GeometryInvalid, skip.Reason);
        Assert.Equal(3, skip.Record);
        Assert.Equal(["A", "C"], decoded.Pages.SelectMany(page => page.Features).Select(feature => feature["name"].StringValue));
    }

    [Fact]
    public void A_ragged_csv_row_is_dropped_and_reported()
    {
        var body = "lon,lat,name\n1,2,A\n3,4\n5,6,C\n";

        var decoded = Decode(body, IngestFormat.Csv, new DecodeOptions { SkipMalformed = true });

        Assert.Equal(IngestSkipReason.RecordMalformed, Assert.Single(decoded.Report.Skipped).Reason);
        Assert.Equal(2, decoded.Report.FeaturesEmitted);
    }

    [Fact]
    public void A_malformed_newline_delimited_line_is_dropped_and_reported()
    {
        var body = string.Join(
            "\n",
            """{"type":"Feature","properties":{"n":1},"geometry":null}""",
            "{not json",
            """{"type":"Feature","properties":{"n":3},"geometry":null}""");

        var decoded = Decode(body, IngestFormat.NewlineDelimitedGeoJson, new DecodeOptions { SkipMalformed = true });

        Assert.Equal(2, decoded.Report.FeaturesEmitted);
        var skip = Assert.Single(decoded.Report.Skipped);
        Assert.Equal(IngestSkipReason.RecordMalformed, skip.Reason);
        Assert.Equal(2, skip.Record);
    }

    [Fact]
    public void A_bad_geometry_in_a_feature_collection_is_dropped_and_reported()
    {
        var body = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","properties":{"n":1},"geometry":null},
              {"type":"Feature","properties":{"n":2},"geometry":{"type":"Point","coordinates":"nope"}}]}
            """;

        var decoded = Decode(body, IngestFormat.GeoJson, new DecodeOptions { SkipMalformed = true });

        Assert.Equal(1, decoded.Report.FeaturesEmitted);
        Assert.Equal(IngestSkipReason.GeometryInvalid, Assert.Single(decoded.Report.Skipped).Reason);
    }

    [Fact]
    public void The_skip_list_is_bounded_but_the_count_is_exact()
    {
        var rows = string.Join("\n", Enumerable.Range(0, DecodeReport.MaxRecordedSkips + 25).Select(i => $"{i},x,A"));
        var body = $"lon,lat,name\n0,0,good\n{rows}\n";

        var report = Decode(body, IngestFormat.Csv, new DecodeOptions { SkipMalformed = true }).Report;

        Assert.Equal(DecodeReport.MaxRecordedSkips + 25, report.SkippedCount);
        Assert.Equal(DecodeReport.MaxRecordedSkips, report.Skipped.Count);
        Assert.True(report.SkipsTruncated);
    }

    [Fact]
    public void The_report_carries_the_final_schema()
    {
        var body = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","properties":{"a":1},"geometry":null}]}
            """;

        var decoded = Decode(body, IngestFormat.GeoJson, new DecodeOptions { GeometryField = "geom" });

        Assert.Equal(decoded.Schema, decoded.Report.Schema);
        Assert.Equal("geom", decoded.Report.Schema[^1].Name);
    }
}
