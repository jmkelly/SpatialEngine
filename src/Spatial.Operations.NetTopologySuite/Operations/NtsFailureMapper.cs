using NetTopologySuite.Geometries;
using Spatial.PluginSdk.Capabilities;

namespace Spatial.Operations.NetTopologySuite.Operations;

/// <summary>
/// Uniform failure mapping for the NTS operation runners (ADR-0026): an
/// argument-shape problem is an input-contract violation, and so is an input
/// geometry the algorithm cannot process — NTS reports those by throwing
/// <see cref="TopologyException"/> or <see cref="ArgumentException"/> (for
/// example a degenerate ring its LinearRing factory rejects). Anything else
/// is an unexpected provider failure.
/// </summary>
internal static class NtsFailureMapper
{
    public static CapabilityError For(CapabilityId capability, Exception exception)
    {
        if (exception is TopologyException or ArgumentException or FormatException)
        {
            return CapabilityError.InvalidArguments(
                $"{capability} could not process the input geometry: {exception.Message}");
        }

        return CapabilityError.ProviderFailure(
            $"{capability} failed unexpectedly while running the NetTopologySuite algorithm: {exception.Message}");
    }
}