using Spatial.Contracts.TransformationSearch;

namespace Spatial.Transformations.ProjNet;

/// <summary>
/// Helmert algebra for the datum-transformation graph (ADR-0087). A seven-
/// parameter position-vector transformation is the similarity
/// <c>target = s·R·source + T</c> in the geocentric frame, so composing,
/// inverting and reducing one to a three-parameter translation are matrix
/// operations. ProjNet applies exactly this path (source ellipsoid to
/// geocentric, the datum's TOWGS84 forward, the target's TOWGS84 in reverse),
/// which is why the composed parameters the graph publishes describe the
/// transform the engine really performs.
///
/// Rotations in this catalogue are hundredths of an arcsecond, far inside the
/// small-angle regime, so a composed rotation is read back to first order —
/// half the antisymmetric part of the matrix — after an exact recovery of
/// the scale from the determinant. The error that leaves is orders of
/// magnitude below the metre-level accuracy every datum shift here is stated
/// at.
/// </summary>
internal static class HelmertAlgebra
{
    private const double ArcSecondsToRadians = Math.PI / (180.0 * 3600.0);
    private const double RadiansToArcSeconds = 1.0 / ArcSecondsToRadians;
    private const double PpmToScale = 1e-6;

    /// <summary>The mean Earth radius used to bound a dropped rotation at the surface (metres).</summary>
    private const double MeanRadiusMetres = 6_371_000.0;

    /// <summary>
    /// Whether a transformation moves a point by less than a micrometre: a
    /// null datum shift, which is a real answer (two datums that already
    /// realise WGS 84 identically) rather than a missing one. The contract
    /// records carry the numbers; the rules about them live here.
    /// </summary>
    public static bool IsNull(HelmertParameters parameters) =>
        Math.Abs(parameters.Tx) < 1e-6 && Math.Abs(parameters.Ty) < 1e-6 && Math.Abs(parameters.Tz) < 1e-6
        && Math.Abs(parameters.Rx) < 1e-9 && Math.Abs(parameters.Ry) < 1e-9 && Math.Abs(parameters.Rz) < 1e-9
        && Math.Abs(parameters.ScalePpm) < 1e-9;

    /// <summary>
    /// The transform equivalent to applying <paramref name="first"/> and then
    /// <paramref name="second"/> — the geocentric composition of two datum
    /// steps, which is how a direct shift between two datums is published.
    /// </summary>
    public static HelmertParameters Compose(HelmertParameters first, HelmertParameters second)
    {
        var a = Multiply(Linear(second), Linear(first));
        var b = Add(Apply(Linear(second), new[] { first.Tx, first.Ty, first.Tz }), new[] { second.Tx, second.Ty, second.Tz });
        return From(a, b);
    }

    /// <summary>
    /// The inverse of a position-vector transformation: the rotations and
    /// scale negate exactly, and the translation follows from the inverse
    /// linear part. A reversed operation is a different operation, which is
    /// what <c>transformForward: false</c> tells a client.
    /// </summary>
    public static HelmertParameters Invert(HelmertParameters parameters)
    {
        var a = Invert(Linear(parameters));
        var b = Scale(Apply(a, new[] { parameters.Tx, parameters.Ty, parameters.Tz }), -1.0);
        return From(a, b);
    }

    /// <summary>
    /// The three-parameter geocentric translation of a transformation: the
    /// translation alone, dropping the rotation and scale terms. It is a real
    /// (if cruder) operation — the one older toolchains use when they cannot
    /// carry a full Helmert.
    /// </summary>
    public static HelmertParameters ToTranslation(HelmertParameters parameters) =>
        new(parameters.Tx, parameters.Ty, parameters.Tz, 0.0, 0.0, 0.0, 0.0);

    /// <summary>
    /// A first-order bound on the ground error the dropped rotation and scale
    /// terms of a transformation cause: <c>‖A − s·I‖·R</c>, the deviation of
    /// the linear part from the identity times the Earth's radius. This is how
    /// the graph states an accuracy for the reduced operation instead of
    /// inventing a round number.
    /// </summary>
    public static double DroppedLinearResidualMetres(HelmertParameters parameters)
    {
        var a = Linear(parameters);
        var scale = (a[0][0] + a[1][1] + a[2][2]) / 3.0;
        var deviation = 0.0;
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                var identity = row == column ? scale : 0.0;
                var difference = a[row][column] - identity;
                deviation += difference * difference;
            }
        }

        return Math.Sqrt(deviation) * MeanRadiusMetres;
    }

    private static HelmertParameters From(double[][] linear, double[] translation)
    {
        // The linear part is a scaled rotation, so its determinant is the
        // cube of the scale: recovering the scale that way is exact, and only
        // the rotation angles are read back to first order.
        var scale = Math.Cbrt(Determinant(linear));
        var rotation = Divide(linear, scale);
        return new HelmertParameters(
            translation[0],
            translation[1],
            translation[2],
            (rotation[2][1] - rotation[1][2]) * RadiansToArcSeconds / 2.0,
            (rotation[0][2] - rotation[2][0]) * RadiansToArcSeconds / 2.0,
            (rotation[1][0] - rotation[0][1]) * RadiansToArcSeconds / 2.0,
            (scale - 1.0) / PpmToScale);
    }

    private static double Determinant(double[][] matrix) =>
        matrix[0][0] * (matrix[1][1] * matrix[2][2] - matrix[1][2] * matrix[2][1])
        - matrix[0][1] * (matrix[1][0] * matrix[2][2] - matrix[1][2] * matrix[2][0])
        + matrix[0][2] * (matrix[1][0] * matrix[2][1] - matrix[1][1] * matrix[2][0]);

    /// <summary>The similarity's linear part <c>s·R</c>: a rotation about each
    /// axis, then the uniform scale.</summary>
    private static double[][] Linear(HelmertParameters parameters)
    {
        var rx = parameters.Rx * ArcSecondsToRadians;
        var ry = parameters.Ry * ArcSecondsToRadians;
        var rz = parameters.Rz * ArcSecondsToRadians;
        var rotation = new[]
        {
            new[]
            {
                Math.Cos(rz) * Math.Cos(ry),
                Math.Cos(rz) * Math.Sin(ry) * Math.Sin(rx) - Math.Sin(rz) * Math.Cos(rx),
                Math.Cos(rz) * Math.Sin(ry) * Math.Cos(rx) + Math.Sin(rz) * Math.Sin(rx),
            },
            new[]
            {
                Math.Sin(rz) * Math.Cos(ry),
                Math.Sin(rz) * Math.Sin(ry) * Math.Sin(rx) + Math.Cos(rz) * Math.Cos(rx),
                Math.Sin(rz) * Math.Sin(ry) * Math.Cos(rx) - Math.Cos(rz) * Math.Sin(rx),
            },
            new[]
            {
                -Math.Sin(ry),
                Math.Cos(ry) * Math.Sin(rx),
                Math.Cos(ry) * Math.Cos(rx),
            },
        };
        var scale = 1.0 + parameters.ScalePpm * PpmToScale;
        return rotation
            .Select(row => row.Select(value => scale * value).ToArray())
            .ToArray();
    }

    private static double[][] Multiply(double[][] left, double[][] right) =>
        [.. Enumerable.Range(0, 3).Select(row => Enumerable.Range(0, 3).Select(column =>
            left[row][0] * right[0][column] + left[row][1] * right[1][column] + left[row][2] * right[2][column]).ToArray())];

    private static double[][] Invert(double[][] matrix)
    {
        // A scaled rotation's inverse is its transpose over the square of its
        // scale: the adjugate/determinant form would divide by s^3 rather
        // than s^2 and leave the scale doubled.
        var scale = Math.Cbrt(Determinant(matrix));
        var scaleSquared = scale * scale;
        var transpose = new[]
        {
            new[] { matrix[0][0], matrix[1][0], matrix[2][0] },
            new[] { matrix[0][1], matrix[1][1], matrix[2][1] },
            new[] { matrix[0][2], matrix[1][2], matrix[2][2] },
        };
        return transpose.Select(row => row.Select(value => value / scaleSquared).ToArray()).ToArray();
    }

    private static double[] Apply(double[][] matrix, double[] vector) =>
        Enumerable.Range(0, 3).Select(row => matrix[row][0] * vector[0] + matrix[row][1] * vector[1] + matrix[row][2] * vector[2]).ToArray();

    private static double[] Add(double[] left, double[] right) => [left[0] + right[0], left[1] + right[1], left[2] + right[2]];

    private static double[] Scale(double[] vector, double factor) => [vector[0] * factor, vector[1] * factor, vector[2] * factor];

    private static double[][] Divide(double[][] matrix, double factor) =>
        matrix.Select(row => row.Select(value => value / factor).ToArray()).ToArray();
}
