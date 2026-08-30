using Spatial.Core.Geometry;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Transformations;
using Spatial.Transformations.ProjNet;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// Shared invocation helpers for the ProjNet transformation provider tests:
/// thin wrappers that build contract invocations and unwrap outcomes, so the
/// test classes read as capability-level assertions.
/// </summary>
internal static class TransformInvoker
{
    private static readonly ProjNetTransformationsProvider Provider = new();

    /// <summary>Invokes <c>spatial.coordinate.transform@1</c> with the given argument pairs.</summary>
    public static ValueTask<CapabilityResult> TransformAsync(params object?[] pairs) =>
        Provider.InvokeAsync(CapabilityInvocation.Create(TransformContract.Id, Arguments(pairs)));

    /// <summary>Invokes <c>spatial.crs.describe@1</c> with the given argument pairs.</summary>
    public static ValueTask<CapabilityResult> DescribeAsync(params object?[] pairs) =>
        Provider.InvokeAsync(CapabilityInvocation.Create(CrsDescribeContract.Id, Arguments(pairs)));

    public static ValueTask<CapabilityResult> InvokeAsync(CapabilityId capability, IReadOnlyDictionary<string, object?> arguments) =>
        Provider.InvokeAsync(CapabilityInvocation.Create(capability, arguments));

    public static ValueTask<CapabilityResult> InvokeAsync(
        CapabilityId capability,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken) =>
        Provider.InvokeAsync(CapabilityInvocation.Create(capability, arguments) with
        {
            CancellationToken = cancellationToken,
        });

    /// <summary>Builds the argument dictionary from name/value pairs.</summary>
    public static IReadOnlyDictionary<string, object?> Arguments(params object?[] pairs)
    {
        var arguments = new Dictionary<string, object?>(pairs.Length / 2);
        for (var index = 0; index < pairs.Length; index += 2)
        {
            arguments[(string)pairs[index]!] = pairs[index + 1];
        }

        return arguments;
    }

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
