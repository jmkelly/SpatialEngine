using Spatial.Contracts.TransformationSearch;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The Helmert algebra the graph is built on (ADR-0074): composing, inverting
/// and reducing a position-vector transformation. These are the properties the
/// published parameters rest on, so they are pinned independently of any
/// candidate that uses them — an inverse that is off by the 20 ppm scale
/// would still produce plausible-looking parameters.
/// </summary>
public sealed class HelmertAlgebraTests
{
    private static readonly HelmertParameters Osgb36 = new(446.448, -125.157, 542.060, 0.15, 0.247, 0.842, -20.489);

    [Fact]
    public void Inverting_and_recomposing_returns_the_original_transformation()
    {
        var east = new HelmertParameters(-89.5, -93.8, -123.1, 0.156, 0.194, 0.219, 4.6);

        // A step and its own inverse cancel, and the inverse of a composed
        // operation is the reverse composition of the inverses.
        AssertClose(new HelmertParameters(0, 0, 0, 0, 0, 0, 0), HelmertAlgebra.Compose(Osgb36, HelmertAlgebra.Invert(Osgb36)));
        AssertClose(
            HelmertAlgebra.Invert(HelmertAlgebra.Compose(Osgb36, east)),
            HelmertAlgebra.Compose(HelmertAlgebra.Invert(east), HelmertAlgebra.Invert(Osgb36)));
    }

    [Fact]
    public void Composing_with_an_identity_transformation_changes_nothing()
    {
        var identity = new HelmertParameters(0, 0, 0, 0, 0, 0, 0);

        AssertClose(Osgb36, HelmertAlgebra.Compose(Osgb36, identity));
        AssertClose(Osgb36, HelmertAlgebra.Compose(identity, Osgb36));
    }

    [Fact]
    public void The_reduced_form_keeps_the_translation_and_drops_the_rest()
    {
        var reduced = HelmertAlgebra.ToTranslation(Osgb36);

        Assert.Equal(Osgb36.Tx, reduced.Tx, 9);
        Assert.Equal(Osgb36.Ty, reduced.Ty, 9);
        Assert.Equal(Osgb36.Tz, reduced.Tz, 9);
        Assert.Equal(0.0, reduced.Rx, 9);
        Assert.Equal(0.0, reduced.Ry, 9);
        Assert.Equal(0.0, reduced.Rz, 9);
        Assert.Equal(0.0, reduced.ScalePpm, 9);
    }

    [Fact]
    public void A_dropped_rotation_costs_what_the_scale_alone_would_not()
    {
        // A translation-only transformation is its own reduction, so dropping
        // its (absent) rotations costs nothing.
        Assert.Equal(0.0, HelmertAlgebra.DroppedLinearResidualMetres(new HelmertParameters(446.448, 0, 0, 0, 0, 0, 0)), 9);

        // OSGB36's rotations are nearly an arcsecond at the Earth's radius.
        var residual = HelmertAlgebra.DroppedLinearResidualMetres(Osgb36);
        Assert.InRange(residual, 5.0, 50.0);
    }

    [Fact]
    public void A_null_shift_is_recognised()
    {
        Assert.True(HelmertAlgebra.IsNull(new HelmertParameters(0, 0, 0, 0, 0, 0, 0)));
        Assert.True(HelmertAlgebra.IsNull(HelmertAlgebra.Invert(new HelmertParameters(0, 0, 0, 0, 0, 0, 0))));
        Assert.False(HelmertAlgebra.IsNull(Osgb36));
    }

    private static void AssertClose(HelmertParameters expected, HelmertParameters actual)
    {
        // Rotations are read back to first order, so a round trip through the
        // algebra keeps them to a thousandth of an arcsecond rather than to
        // the last digit - nanometres on the ground, and far inside every
        // accuracy the catalogue states.
        const double Tolerance = 1e-6;
        const double RotationTolerance = 1e-3;
        Assert.Equal(expected.Tx, actual.Tx, Tolerance);
        Assert.Equal(expected.Ty, actual.Ty, Tolerance);
        Assert.Equal(expected.Tz, actual.Tz, Tolerance);
        Assert.Equal(expected.Rx, actual.Rx, RotationTolerance);
        Assert.Equal(expected.Ry, actual.Ry, RotationTolerance);
        Assert.Equal(expected.Rz, actual.Rz, RotationTolerance);
        Assert.Equal(expected.ScalePpm, actual.ScalePpm, Tolerance);
    }
}
