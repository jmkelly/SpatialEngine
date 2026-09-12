using System.Text;

namespace Spatial.Interop.Ingest.Tests;

public sealed class DatasetDecoderTests
{
    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    private const string OneFeature = """{"type":"Feature","properties":{},"geometry":null}""";

    [Fact]
    public void A_non_positive_batch_size_is_rejected()
    {
        Assert.Throws<IngestFormatException>(
            () => DatasetDecoder.Decode(Stream(OneFeature), IngestFormat.GeoJson, new DecodeOptions { BatchSize = 0 }));
    }

    [Fact]
    public void An_empty_geometry_field_is_rejected()
    {
        Assert.Throws<IngestFormatException>(
            () => DatasetDecoder.Decode(Stream(OneFeature), IngestFormat.GeoJson, new DecodeOptions { GeometryField = " " }));
    }

    [Fact]
    public void An_unsupported_format_is_rejected()
    {
        Assert.Throws<IngestFormatException>(() => DatasetDecoder.Decode(Stream("{}"), (IngestFormat)99));
    }

    [Fact]
    public void An_empty_feature_collection_is_rejected()
    {
        Assert.Throws<IngestFormatException>(
            () => DatasetDecoder.Decode(Stream("""{"type":"FeatureCollection","features":[]}"""), IngestFormat.GeoJson));
    }

    [Fact]
    public void The_geometry_field_defaults_to_geometry()
    {
        var decoded = DatasetDecoder.Decode(Stream(OneFeature), IngestFormat.GeoJson);

        Assert.Equal("geometry", decoded.Schema[0].Name);
    }
}
