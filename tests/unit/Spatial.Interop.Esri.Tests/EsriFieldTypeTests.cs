using Spatial.Core.Features;

namespace Spatial.Interop.Esri.Tests;

public sealed class EsriFieldTypeTests
{
    [Theory]
    [InlineData(EsriFieldType.Oid, AttributeKind.Int64)]
    [InlineData(EsriFieldType.Integer, AttributeKind.Int64)]
    [InlineData(EsriFieldType.SmallInteger, AttributeKind.Int64)]
    [InlineData(EsriFieldType.Double, AttributeKind.Double)]
    [InlineData(EsriFieldType.Single, AttributeKind.Double)]
    [InlineData(EsriFieldType.String, AttributeKind.String)]
    [InlineData(EsriFieldType.Date, AttributeKind.DateTimeOffset)]
    [InlineData(EsriFieldType.Guid, AttributeKind.Guid)]
    [InlineData(EsriFieldType.GlobalId, AttributeKind.Guid)]
    [InlineData(EsriFieldType.Geometry, AttributeKind.Geometry)]
    public void Known_types_map(string esriType, AttributeKind kind)
    {
        Assert.True(EsriFieldType.TryToAttributeKind(esriType, out var mapped));
        Assert.Equal(kind, mapped);
    }

    [Fact]
    public void An_unknown_type_is_reported_with_a_reason()
    {
        Assert.False(EsriFieldType.TryToAttributeKind("esriFieldTypeRaster", out _, out var error));
        Assert.Contains("Raster", error);
    }

    [Theory]
    [InlineData(AttributeKind.Boolean)]
    [InlineData(AttributeKind.Int64)]
    [InlineData(AttributeKind.Double)]
    [InlineData(AttributeKind.String)]
    [InlineData(AttributeKind.Geometry)]
    [InlineData(AttributeKind.DateTimeOffset)]
    [InlineData(AttributeKind.Guid)]
    public void Core_kinds_round_trip_through_the_type_map(AttributeKind kind)
    {
        var esriType = EsriFieldType.FromAttributeKind(kind);

        Assert.True(EsriFieldType.TryToAttributeKind(esriType, out var mapped));
        Assert.True(mapped == kind || (kind == AttributeKind.Boolean && mapped == AttributeKind.Int64));
    }

    [Fact]
    public void The_null_kind_has_no_field_type()
    {
        Assert.Throws<EsriInteropException>(() => EsriFieldType.FromAttributeKind(AttributeKind.Null));
    }
}
