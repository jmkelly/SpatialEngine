namespace Spatial.Adapter.GeoServices.Tests;

public sealed class EsriLayerModelTests
{
    [Theory]
    [InlineData("point", "esriGeometryPoint")]
    [InlineData("POINT", "esriGeometryPoint")]
    [InlineData("  point  ", "esriGeometryPoint")]
    [InlineData("multipoint", "esriGeometryMultipoint")]
    [InlineData("linestring", "esriGeometryPolyline")]
    [InlineData("multilinestring", "esriGeometryPolyline")]
    [InlineData("line", "esriGeometryPolyline")]
    [InlineData("linearring", "esriGeometryPolyline")]
    [InlineData("polygon", "esriGeometryPolygon")]
    [InlineData("multipolygon", "esriGeometryPolygon")]
    [InlineData("unknown", "esriGeometryNull")]
    [InlineData("", "esriGeometryNull")]
    public void GeometryType_maps_engine_names_to_esri_constants(string engineType, string expected)
    {
        Assert.Equal(expected, EsriLayerModel.GeometryType(engineType));
    }
}
