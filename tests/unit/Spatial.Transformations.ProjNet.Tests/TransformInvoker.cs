using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Transformations;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// Shared test helpers for the ProjNet transform tests: thin wrappers over
/// <see cref="ProjNetTransforms"/> that accept contract-style name/value
/// pairs (including absent or mistyped values) and surface failures as
/// <see cref="SpatialException"/>, so the tests read as service-level
/// assertions.
/// </summary>
internal static class TransformInvoker
{
    private static readonly ProjNetTransforms Service = new();

    public static Task<CrsDescription> DescribeAsync(params object?[] pairs) =>
        Task.FromResult(Service.Describe(RequireString(pairs, "crs")));

    public static Task<CrsDescription> DescribeAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) =>
        Task.FromResult(Service.Describe(RequireString(arguments, "crs"), cancellationToken));

    public static Task<IGeometry> TransformAsync(params object?[] pairs) =>
        TransformAsync(Arguments(pairs), CancellationToken.None);

    public static Task<IGeometry> TransformAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetValue("geometry", out var raw) || raw is not IGeometry geometry)
        {
            throw SpatialException.BadArguments("The transform requires 'geometry' to carry a spatial geometry.");
        }

        string? source = null;
        if (arguments.TryGetValue("source", out var sourceRaw) && sourceRaw is not null)
        {
            source = sourceRaw as string
                ?? throw SpatialException.BadArguments("'source' must be a CRS identity (authority:code).");
        }

        var target = RequireString(arguments, "target");
        return Task.FromResult(Service.Transform(geometry, source, target, cancellationToken));
    }

    public static Task<IGeometry> InvokeAsync(object? capability, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) =>
        TransformAsync(arguments, cancellationToken);

    public static IReadOnlyDictionary<string, object?> Arguments(params object?[] pairs)
    {
        var arguments = new Dictionary<string, object?>(pairs.Length / 2);
        for (var index = 0; index < pairs.Length; index += 2)
        {
            arguments[(string)pairs[index]!] = pairs[index + 1];
        }

        return arguments;
    }

    private static string RequireString(IReadOnlyDictionary<string, object?> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var raw) || raw is not string text)
        {
            throw SpatialException.BadArguments($"The transform requires '{name}' to carry a CRS identity string such as 'EPSG:4326'.");
        }

        return text;
    }

    private static string RequireString(object?[] pairs, string name) =>
        RequireString(Arguments(pairs), name);

    /// <summary>The authoritative control-point values, generated with PROJ 9 (pyproj), always_xy=true.</summary>
    public static class ControlPoints
    {
        public static readonly (double Lon, double Lat) Berlin = (13.405, 52.52);

        public static readonly (double X, double Y) BerlinUtm32 = (798812.8026, 5827999.9001);

        public static readonly (double Lon, double Lat) London = (-0.1276, 51.5072);

        public static readonly (double X, double Y) LondonWebMercator = (-14204.367025, 6711506.705401);

        public static readonly (double X, double Y) LondonBritishNationalGrid = (530043.194981, 180358.208620);

        public static readonly (double Lon, double Lat) Munich = (11.572, 48.14);

        public static readonly (double X, double Y) MunichEtrsUtm32 = (691333.003321, 5335060.269267);

        public static readonly (double Lon, double Lat) Lyon = (4.85, 45.75);

        public static readonly (double X, double Y) LyonLambert93 = (843814.2437, 6518396.0189);

        public static readonly (double Lon, double Lat) Seattle = (-122.33, 47.61);

        public static readonly (double X, double Y) SeattleNad83Utm10 = (550354.4039, 5273172.2752);
    }
}
