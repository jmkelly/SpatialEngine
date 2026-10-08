namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The parameter-name rule: a latitude of standard parallel only belongs to
/// the polar stereographic variant B method, so a document that states one
/// anywhere else is refused by name rather than served with a parameter its
/// projection does not read.
/// </summary>
public sealed class ProjWktParameterTests
{
    private const string TransverseMercatorWithAStandardParallel =
        """
        PROJCS["Test TM",GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137,298.257223563]],PRIMEM["Greenwich",0],UNIT["Degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],PARAMETER["False_Easting",500000],PARAMETER["False_Northing",0],PARAMETER["Central_Meridian",-123],PARAMETER["Scale_Factor",0.9996],PARAMETER["Latitude_Of_Origin",0],PARAMETER["Latitude of standard parallel",-71],UNIT["Meter",1]]
        """;

    [Fact]
    public void A_standard_parallel_outside_the_polar_stereographic_variant_B_is_refused_by_name()
    {
        Assert.False(ProjWkt.TryParse(TransverseMercatorWithAStandardParallel, out var definition, out var error));
        Assert.Null(definition);
        Assert.NotNull(error);
        Assert.Contains("latitude of standard parallel", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unknown_projection_parameter_is_refused_by_name()
    {
        var wkt = TransverseMercatorWithAStandardParallel.Replace(
            "Latitude of standard parallel", "Spurious parameter", StringComparison.Ordinal);
        Assert.False(ProjWkt.TryParse(wkt, out var definition, out var error));
        Assert.Null(definition);
        Assert.NotNull(error);
        Assert.Contains("Spurious parameter", error, StringComparison.Ordinal);
    }
}
