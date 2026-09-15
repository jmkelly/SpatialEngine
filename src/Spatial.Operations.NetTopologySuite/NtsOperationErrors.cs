using Spatial.Contracts;
using NtsTopologyException = NetTopologySuite.Geometries.TopologyException;

namespace Spatial.Operations.NetTopologySuite;

/// <summary>
/// Maps NTS and argument failures onto the engine's single error type
/// (ADR-0033): invalid inputs and unprocessable geometry become
/// <c>invalid.arguments</c>; anything else is a provider failure.
/// </summary>
internal static class NtsOperationErrors
{
    public static SpatialException Map(Exception exception, string operation) =>
        exception is SpatialException spatial ? spatial
        : exception is NtsTopologyException or ArgumentException or FormatException
            ? SpatialException.BadArguments($"The operation '{operation}' could not process the input geometry: {exception.Message}")
            : new SpatialException("provider.failure", $"The operation '{operation}' failed: {exception.Message}", exception);
}
