using Spatial.Contracts;
using Spatial.Contracts.Transformations;
using Spatial.Core.Geometry;
using static Spatial.Transformations.ProjNet.Tests.TransformInvoker;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The EPSG catalogue of the ProjNet transformation provider
/// (ADR-0027): the enumerated codes the provider has always served, the
/// family/axis/unit descriptions and the unknown-code rejection. Pins the
/// catalogue surface the provider promises through the CRS description
/// service (ADR-0027/ADR-0033). The generated families are pinned by
/// <see cref="ProjNetGeneratedCatalogTests"/>; this file pins the rows that
/// are still written out.
/// </summary>
public sealed class ProjNetCatalogTests
{
    [Fact]
    public async Task The_catalogue_serves_the_documented_EPSG_subset()
    {
        var described = new List<string>();
        foreach (var code in Documented)
        {
            var description = await DescribeAsync("crs", $"EPSG:{code}");
            described.Add(description.Code);
        }

        Assert.Equal(Documented.Select(code => code.ToString(System.Globalization.CultureInfo.InvariantCulture)), described);
    }

    [Fact]
    public async Task Every_code_the_catalogue_serves_describes()
    {
        foreach (var code in ProjEpsgCatalog.Codes)
        {
            var description = await DescribeAsync("crs", $"EPSG:{code}");

            Assert.Equal(code.ToString(System.Globalization.CultureInfo.InvariantCulture), description.Code);
            Assert.NotEmpty(description.Name);
        }
    }

    [Fact]
    public async Task Wgs84_is_a_geographic_lon_lat_degree_CRS()
    {
        var description = await Describe(4326);
        Assert.Equal("EPSG", description.Authority);
        Assert.Equal("4326", description.Code);
        Assert.Equal("WGS 84", description.Name);
        Assert.Equal(CrsKind.Geographic, description.Kind);
        Assert.Equal(2, description.Dimension);
        Assert.Equal("World Geodetic System 1984", description.Datum);
        Assert.Equal("WGS 84", description.Ellipsoid!.Name);
        Assert.Equal(6378137.0, description.Ellipsoid.SemiMajorAxis, 3);
        Assert.Equal(6356752.3142, description.Ellipsoid.SemiMinorAxis, 3);
        Assert.Equal("metre", description.Ellipsoid.UnitName);
        Assert.Equal("Lon", description.Axes[0].Name);
        Assert.Equal(AxisOrientation.East, description.Axes[0].Orientation);
        Assert.Equal("degree", description.Axes[0].UnitName);
        Assert.Equal("Lat", description.Axes[1].Name);
        Assert.Equal(AxisOrientation.North, description.Axes[1].Orientation);
        Assert.Equal("degree", description.Axes[1].UnitName);
    }

    [Fact]
    public async Task British_national_grid_is_a_projected_easting_northing_metre_CRS()
    {
        var description = await Describe(27700);
        Assert.Equal("OSGB36 / British National Grid", description.Name);
        Assert.Equal(CrsKind.Projected, description.Kind);
        Assert.Equal("metre", description.Axes[0].UnitName);
        Assert.Equal("Easting", description.Axes[0].Name);
        Assert.Equal(AxisOrientation.East, description.Axes[0].Orientation);
        Assert.Equal("Northing", description.Axes[1].Name);
        Assert.Equal(AxisOrientation.North, description.Axes[1].Orientation);
        Assert.Equal("Ordnance Survey of Great Britain 1936", description.Datum);
        Assert.Equal("Airy 1830", description.Ellipsoid!.Name);
    }

    [Fact]
    public async Task Web_mercator_is_projected_with_metre_axes()
    {
        var description = await Describe(3857);
        Assert.Equal("WGS 84 / Pseudo-Mercator", description.Name);
        Assert.Equal(CrsKind.Projected, description.Kind);
        Assert.Equal("metre", description.Axes[0].UnitName);
    }

    [Fact]
    public void A_crs_identity_must_be_authority_colon_code()
    {
        Assert.False(CrsIdentity.TryParse(null, out _));
        Assert.False(CrsIdentity.TryParse("", out _));
        Assert.False(CrsIdentity.TryParse("EPSG", out _));
        Assert.False(CrsIdentity.TryParse("EPSG:", out _));
        Assert.False(CrsIdentity.TryParse(":4326", out _));
        Assert.False(CrsIdentity.TryParse("EPSG:43 26", out _));

        Assert.True(CrsIdentity.TryParse("EPSG:4326", out var identity));
        Assert.Equal("EPSG", identity.Authority);
        Assert.Equal("4326", identity.Code);
        Assert.Equal("EPSG:4326", identity.ToString());
    }

    private static Task<CrsDescription> Describe(int code) =>
        DescribeAsync("crs", $"EPSG:{code}");

    /// <summary>
    /// The fifteen codes the catalogue enumerated before the families were
    /// generated, so the served set is a superset of what it was and no
    /// served code disappeared when generation replaced the typed rows.
    /// </summary>
    private static readonly int[] Documented =
    [
        2154, 3857, 4171, 4258, 4269, 4277, 4326,
        25832, 25833, 26910, 27700, 32610, 32612, 32632, 32633,
    ];
}
