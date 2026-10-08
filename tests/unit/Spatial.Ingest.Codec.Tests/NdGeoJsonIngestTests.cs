using System.Text;

namespace Spatial.Ingest.Codec.Tests;

public sealed class NdGeoJsonIngestTests
{
    private static DecodedDataset Decode(string text) =>
        DatasetDecoder.Decode(new MemoryStream(Encoding.UTF8.GetBytes(text)), IngestFormat.NewlineDelimitedGeoJson);

    private static DecodedDataset DecodeWith(string text, DecodeOptions options) =>
        DatasetDecoder.Decode(new MemoryStream(Encoding.UTF8.GetBytes(text)), IngestFormat.NewlineDelimitedGeoJson, options);

    [Fact]
    public void One_feature_per_line_is_decoded_and_blank_lines_are_ignored()
    {
        var decoded = Decode(
            """
            {"type":"Feature","properties":{"n":1},"geometry":{"type":"Point","coordinates":[1,2]}}

            {"type":"Feature","properties":{"n":2},"geometry":null}
            """);

        Assert.Single(decoded.Pages);
        Assert.Equal(2, decoded.Pages[0].Count);
        Assert.Equal(1, decoded.Pages[0][0]["n"].Int64Value);
        Assert.Equal(2, decoded.Pages[0][1]["n"].Int64Value);
    }

    [Fact]
    public void An_invalid_line_reports_its_line_number()
    {
        var exception = Assert.Throws<IngestFormatException>(
            () => Decode("{\"type\":\"Feature\",\"properties\":{},\"geometry\":null}\nnot json\n"));

        Assert.Contains("Line 2", exception.Message);
    }

    [Fact]
    public void An_invalid_first_line_fails_before_any_record_exists()
    {
        var exception = Assert.Throws<IngestFormatException>(() => Decode("not json\n"));

        Assert.Contains("Line 1", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_invalid_first_line_is_dropped_and_reported_when_the_decode_is_told_to_skip()
    {
        var decoded = DecodeWith(
            "not json\n{\"type\":\"Feature\",\"properties\":{\"n\":1},\"geometry\":null}\n",
            new DecodeOptions { SkipMalformed = true });

        Assert.Equal(1, decoded.Report.FeaturesEmitted);
        Assert.Equal(1, decoded.Report.SkippedCount);
        Assert.Equal(1, decoded.Pages[0][0]["n"].Int64Value);
    }

    [Fact]
    public void A_later_record_repeating_the_first_record_crs_decodes()
    {
        var first = "{\"type\":\"Feature\",\"crs\":{\"type\":\"name\",\"properties\":{\"name\":\"urn:ogc:def:crs:EPSG::3857\"}},\"properties\":{},\"geometry\":null}";
        var second = "{\"type\":\"Feature\",\"crs\":{\"type\":\"name\",\"properties\":{\"name\":\"EPSG:3857\"}},\"properties\":{},\"geometry\":null}";

        var decoded = DecodeWith(
            first + "\n" + second + "\n",
            new DecodeOptions { Srid = 3857 });

        Assert.Equal(2, decoded.Pages[0].Count);
        Assert.Equal(3857, decoded.Report.Crs.SourceSrid);
    }
}
