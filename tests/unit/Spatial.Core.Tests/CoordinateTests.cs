using Spatial.Core.Geometry;

namespace Spatial.Core.Tests;

public class CoordinateLayoutTests
{
    [Theory]
    [InlineData(CoordinateLayout.Xy, 2, false, false)]
    [InlineData(CoordinateLayout.Xyz, 3, true, false)]
    [InlineData(CoordinateLayout.Xym, 3, false, true)]
    [InlineData(CoordinateLayout.Xyzm, 4, true, true)]
    public void Layout_properties(CoordinateLayout layout, int ordinateCount, bool hasZ, bool hasM)
    {
        Assert.Equal(ordinateCount, layout.OrdinateCount());
        Assert.Equal(hasZ, layout.HasZ());
        Assert.Equal(hasM, layout.HasM());
    }

    [Fact]
    public void Unknown_layout_throws()
    {
        var layout = (CoordinateLayout)99;
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => layout.OrdinateCount());
        Assert.Contains("Unknown coordinate layout", exception.Message);
    }

    [Theory]
    [InlineData(CoordinateLayout.Xyz, true)]
    [InlineData(CoordinateLayout.Xyzm, true)]
    [InlineData(CoordinateLayout.Xy, false)]
    [InlineData(CoordinateLayout.Xym, false)]
    public void HasZ_matches_storage(CoordinateLayout layout, bool expected) =>
        Assert.Equal(expected, layout.HasZ());

    [Theory]
    [InlineData(CoordinateLayout.Xym, true)]
    [InlineData(CoordinateLayout.Xyzm, true)]
    [InlineData(CoordinateLayout.Xy, false)]
    [InlineData(CoordinateLayout.Xyz, false)]
    public void HasM_matches_storage(CoordinateLayout layout, bool expected) =>
        Assert.Equal(expected, layout.HasM());
}

public class CoordinateInferenceTests
{
    [Fact]
    public void Infer_xy_when_no_optional_ordinates() =>
        Assert.Equal(CoordinateLayout.Xy, Infer(new Coordinate(1, 2), new Coordinate(3, 4)));

    [Fact]
    public void Infer_xyz_when_any_z_present() =>
        Assert.Equal(CoordinateLayout.Xyz, Infer(new Coordinate(1, 2, Z: 5), new Coordinate(3, 4)));

    [Fact]
    public void Infer_xym_when_any_m_present() =>
        Assert.Equal(CoordinateLayout.Xym, Infer(new Coordinate(1, 2, M: 6), new Coordinate(3, 4)));

    [Fact]
    public void Infer_xyzm_when_z_and_m_present() =>
        Assert.Equal(CoordinateLayout.Xyzm, Infer(new Coordinate(1, 2, Z: 5), new Coordinate(3, 4, M: 6)));

    [Fact]
    public void Infer_xy_for_empty_span() =>
        Assert.Equal(CoordinateLayout.Xy, Infer());

    private static CoordinateLayout Infer(params Coordinate[] coordinates) =>
        coordinates.AsSpan().Infer();
}

public class CoordinateReferenceTests
{
    [Fact]
    public void Epsg_factory_normalises_identity()
    {
        var crs = CoordinateReference.Epsg(4326);
        Assert.Equal("EPSG", crs.Authority);
        Assert.Equal("4326", crs.Code);
        Assert.Equal("EPSG:4326", crs.ToString());
    }

    [Fact]
    public void Record_equality_is_structural()
    {
        Assert.Equal(CoordinateReference.Epsg(4326), CoordinateReference.Epsg(4326));
        Assert.NotEqual(CoordinateReference.Epsg(4326), CoordinateReference.Epsg(3857));
        Assert.NotEqual(CoordinateReference.Epsg(4326), new CoordinateReference("IGNF", "LAMB93"));
    }

    [Theory]
    [InlineData("", "4326")]
    [InlineData("EPSG", "")]
    [InlineData("   ", "4326")]
    [InlineData("EPSG", " \t ")]
    [InlineData(null, "4326")]
    public void Empty_or_whitespace_identity_is_rejected(string? authority, string code)
    {
        if (authority is null)
        {
            Assert.Throws<ArgumentNullException>(() => new CoordinateReference(null!, code));
        }
        else
        {
            Assert.Throws<ArgumentException>(() => new CoordinateReference(authority, code));
        }
    }
}
