namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// The WMS axis-order rule (ADR-0053 §3): EPSG:4326 is latitude-first only
/// for WMS 1.3.x; older versions and every other identity stay x-first, and
/// a missing version reads the 1.3.0 way.
/// </summary>
public sealed class OgcCrsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("1.3.0")]
    [InlineData("1.3.0 ")]
    public void Epsg4326_is_latitude_first_for_130_or_no_version(string? version)
    {
        var (crs, yFirst) = OgcCrs.Resolve("EPSG:4326", version);

        Assert.Equal("EPSG:4326", crs);
        Assert.True(yFirst);
    }

    [Theory]
    [InlineData("1.1.1")]
    [InlineData("1.1.0")]
    [InlineData("1.0.0")]
    public void Epsg4326_stays_x_first_before_130(string version)
    {
        var (crs, yFirst) = OgcCrs.Resolve("EPSG:4326", version);

        Assert.Equal("EPSG:4326", crs);
        Assert.False(yFirst);
    }

    [Theory]
    [InlineData("CRS:84", "1.3.0")]
    [InlineData("CRS:84", "1.1.1")]
    [InlineData("EPSG:3857", "1.3.0")]
    public void Other_identities_are_always_x_first(string value, string version)
    {
        Assert.False(OgcCrs.Resolve(value, version).YFirst);
    }

    [Fact]
    public void The_versionless_overload_keeps_the_130_order()
    {
        Assert.True(OgcCrs.Resolve("EPSG:4326").YFirst);
        Assert.True(OgcCrs.Resolve("urn:ogc:def:crs:EPSG::4326", "1.3.0").YFirst);
    }
}
