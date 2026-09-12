using System.Text;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Interop.Ingest.Tests;

public sealed class CsvIngestTests
{
    private static DecodedDataset Decode(string text, DecodeOptions? options = null) =>
        DatasetDecoder.Decode(new MemoryStream(Encoding.UTF8.GetBytes(text)), IngestFormat.Csv, options);

    [Fact]
    public void X_and_y_columns_are_auto_detected_and_values_typed()
    {
        var decoded = Decode("x,y,name,count,ratio,active\n1,2,A,3,4.5,true\n");

        Assert.Equal(["name", "count", "ratio", "active", "geometry"], decoded.Schema.Fields.Select(field => field.Name));
        Assert.Equal(AttributeKind.String, decoded.Schema[0].Kind);
        Assert.Equal(AttributeKind.Int64, decoded.Schema[1].Kind);
        Assert.Equal(AttributeKind.Double, decoded.Schema[2].Kind);
        Assert.Equal(AttributeKind.Boolean, decoded.Schema[3].Kind);

        var feature = decoded.Pages[0][0];
        Assert.Equal("A", feature["name"].StringValue);
        Assert.Equal(3, feature["count"].Int64Value);
        Assert.Equal(4.5, feature["ratio"].DoubleValue);
        Assert.True(feature["active"].BooleanValue);
        var point = Assert.IsType<Point>(feature["geometry"].GeometryValue);
        Assert.Equal(1, point.X);
        Assert.Equal(2, point.Y);
    }

    [Fact]
    public void Lon_and_lat_columns_are_auto_detected()
    {
        var decoded = Decode("lon,lat\n1,2\n");

        Assert.IsType<Point>(decoded.Pages[0][0]["geometry"].GeometryValue);
    }

    [Fact]
    public void Explicit_x_and_y_fields_are_used()
    {
        var decoded = Decode("east,north\n5,6\n", new DecodeOptions { XField = "east", YField = "north" });

        var point = Assert.IsType<Point>(decoded.Pages[0][0]["geometry"].GeometryValue);
        Assert.Equal(5, point.X);
        Assert.Equal(6, point.Y);
    }

    [Fact]
    public void A_missing_coordinate_yields_a_null_geometry()
    {
        var decoded = Decode("x,y,name\n1,,A\n");

        Assert.True(decoded.Pages[0][0]["geometry"].IsNull);
    }

    [Fact]
    public void Quoted_fields_keep_commas_and_doubled_quotes()
    {
        var decoded = Decode("x,y,name\n1,2,\"A, B\"\"C\"\n");

        Assert.Equal("A, B\"C", decoded.Pages[0][0]["name"].StringValue);
    }

    [Fact]
    public void An_identity_column_is_reported_and_used()
    {
        var decoded = Decode("x,y,oid\n1,2,7\n", new DecodeOptions { IdentityField = "oid" });

        Assert.Equal("oid", decoded.IdentityField);
        Assert.Equal("7", decoded.Pages[0][0].Id.Value);
    }

    [Fact]
    public void A_row_with_an_empty_value_makes_its_field_nullable()
    {
        var decoded = Decode("x,y,name\n1,2,\n");

        Assert.True(decoded.Schema[0].Nullable);
        Assert.True(decoded.Pages[0][0]["name"].IsNull);
    }

    [Fact]
    public void A_non_numeric_coordinate_fails()
    {
        Assert.Throws<IngestFormatException>(() => Decode("x,y\nabc,2\n"));
    }

    [Fact]
    public void Missing_geometry_columns_fail()
    {
        Assert.Throws<IngestFormatException>(() => Decode("name,value\nA,1\n"));
    }

    [Fact]
    public void Explicit_fields_that_do_not_exist_fail()
    {
        Assert.Throws<IngestFormatException>(
            () => Decode("x,y\n1,2\n", new DecodeOptions { XField = "nope", YField = "y" }));
    }

    [Fact]
    public void A_row_with_the_wrong_width_fails()
    {
        Assert.Throws<IngestFormatException>(() => Decode("x,y\n1,2,3\n"));
    }

    [Fact]
    public void An_empty_document_fails()
    {
        Assert.Throws<IngestFormatException>(() => Decode(string.Empty));
    }

    [Fact]
    public void A_header_without_columns_fails()
    {
        Assert.Throws<IngestFormatException>(() => Decode(","));
    }
}
