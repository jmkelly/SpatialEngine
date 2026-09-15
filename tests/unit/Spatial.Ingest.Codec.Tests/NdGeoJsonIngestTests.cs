using System.Text;

namespace Spatial.Ingest.Codec.Tests;

public sealed class NdGeoJsonIngestTests
{
    private static DecodedDataset Decode(string text) =>
        DatasetDecoder.Decode(new MemoryStream(Encoding.UTF8.GetBytes(text)), IngestFormat.NewlineDelimitedGeoJson);

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
}
