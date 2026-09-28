using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The adapter's choke point for Esri failures (spec §9.3): every failure
/// intent the serving surface can express maps here to its Esri code, so
/// leaf engines name this vocabulary — not the interop error model — and
/// the wire codec stays depended on by few. The exceptions produced are
/// identical to constructing <see cref="EsriInteropException"/> directly;
/// only the construction site moves.
/// </summary>
internal static class GeoServicesErrors
{
    internal static EsriInteropException Invalid(string message) => EsriInteropException.Invalid(message);

    internal static EsriInteropException Invalid(string message, Exception inner) => EsriInteropException.Invalid(message, inner);

    internal static EsriInteropException NotFound(string message) => new(EsriErrorCodes.NotFound, message);

    internal static EsriInteropException ServerError(string message) => new(EsriErrorCodes.ServerError, message);

    internal static EsriInteropException ServiceUnavailable(string message) => new(EsriErrorCodes.ServiceUnavailable, message);

    internal static EsriInteropException TokenRequired(string message) => new(EsriErrorCodes.TokenRequired, message);

    internal static EsriInteropException InvalidToken(string message) => new(EsriErrorCodes.InvalidToken, message);

    /// <summary>
    /// A <c>relationshipId</c> that names no declaration on a layer which
    /// declares several, naming the declarations on offer so the client can
    /// address one without a second metadata call.
    /// </summary>
    internal static EsriInteropException RequireRelationshipId(int layerId, IReadOnlyList<LayerRelationship> declared) =>
        Invalid(
            $"The 'relationshipId' parameter is required: layer {layerId} declares {declared.Count} relationships ({string.Join(", ", declared.Select(relationship => relationship.Name))}).");
}
