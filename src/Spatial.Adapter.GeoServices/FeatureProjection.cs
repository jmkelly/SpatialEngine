using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Per-feature projection for query responses: <c>outSR</c> reprojection,
/// <c>geometryPrecision</c> rounding, then the
/// <c>maxAllowableOffset</c>/<c>quantizationParameters</c> generalization.
/// Split out of <see cref="FeatureQueryEngine"/> so the query facade keeps only
/// orchestration and the shaping fan-out lives with the code that uses
/// it (ADR-0040). The rounding itself lives in <see cref="GeometryRounding"/>
/// and the generalization in <see cref="GeometryGeneralization"/>.
/// </summary>
internal static class FeatureProjection
{
    internal static MatchedFeature TransformFeature(
        MatchedFeature match,
        EsriFeatureQuery query,
        CoordinateReference? layerCrs,
        ICoordinateTransforms transforms,
        IGeometryOperations operations,
        CancellationToken cancellationToken)
    {
        var feature = match.Feature;
        var geometryIndex = FeatureGeometry.Index(feature.Schema);
        if (geometryIndex < 0 || feature[geometryIndex].Kind != AttributeKind.Geometry)
        {
            return match;
        }

        var geometry = feature[geometryIndex].GeometryValue;
        if (query.OutSr is { } target && layerCrs is not null && target != layerCrs)
        {
            geometry = transforms.Transform(geometry, layerCrs.Value.ToString(), target.ToString(), cancellationToken);
        }

        if (query.GeometryPrecision is { } precision)
        {
            geometry = GeometryRounding.Round(geometry, precision);
        }

        geometry = GeometryGeneralization.Generalize(geometry, query, operations, cancellationToken);
        if (ReferenceEquals(geometry, feature[geometryIndex].GeometryValue))
        {
            return match;
        }

        var attributes = feature.Attributes.ToArray();
        attributes[geometryIndex] = AttributeValue.FromGeometry(geometry);
        return new MatchedFeature(match.ObjectId, new Feature(feature.Id, feature.Schema, attributes));
    }

    internal static IGeometry TransformGeometry(
        IGeometry geometry,
        CoordinateReference? source,
        CoordinateReference? target,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        if (target is not { } to || source is not { } from || from == to)
        {
            return geometry;
        }

        return transforms.Transform(geometry, from.ToString(), to.ToString(), cancellationToken);
    }

    /// <summary>
    /// The geometry a query matches against: the request's own
    /// <c>geometry</c>, reprojected into the layer's CRS and widened by the
    /// <c>distance</c>/<c>units</c> band (spec §9.1.4). Widening is a
    /// buffer in the layer CRS — the same transform-then-buffer the Geometry
    /// Service uses — so a feature matches when it comes within the band of
    /// the query geometry, and every <c>spatialRel</c> then runs over the
    /// band. Returns the geometry unchanged when there is no band, no
    /// geometry, an unprojected layer, or the two CRSs already agree.
    /// </summary>
    internal static IGeometry? MatchGeometry(QueryGeometryRequest request, CancellationToken cancellationToken)
    {
        var geometry = TransformQueryGeometry(request.Query.Geometry, request.LayerCrs, request.Services.Transforms, cancellationToken);
        return geometry is null || request.Query.Distance is not { } band
            ? geometry
            : Buffer(geometry, band, request, cancellationToken);
    }

    /// <summary>
    /// The band as a buffer of the query geometry. A zero band is left
    /// alone: buffering a point by zero yields an empty geometry, and "no
    /// distance" is exactly the unbuffered query.
    /// </summary>
    private static IGeometry Buffer(
        IGeometry geometry, EsriQueryDistance band, QueryGeometryRequest request, CancellationToken cancellationToken)
    {
        if (band.Value == 0)
        {
            return geometry;
        }

        return request.Services.Operations.Buffer(geometry, BandInLayerUnits(band, request, cancellationToken), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The band expressed in the layer CRS's own units: a curated unit code
    /// is converted with the same linear/angular rule the Geometry Service
    /// applies to <c>unit</c>, and an absent code is already in those units.
    /// </summary>
    private static double BandInLayerUnits(EsriQueryDistance band, QueryGeometryRequest request, CancellationToken cancellationToken)
    {
        if (band.UnitsCode is not { } code)
        {
            return band.Value;
        }

        if (request.LayerCrs is not { } layerCrs)
        {
            throw GeoServicesErrors.Invalid(
                $"The 'units' parameter needs a layer spatial reference to convert unit {code} into; the layer is unprojected.");
        }

        var kind = request.Services.Catalogue.Describe(layerCrs.ToString(), cancellationToken).Kind;
        return band.Value * EsriUnitCode.Factor(code, layerCrs, kind);
    }

    /// <summary>
    /// Reprojects a query's own <c>geometry</c> into the layer's CRS so the
    /// spatial match runs in the coordinates the features are stored in.
    /// Returns the geometry unchanged when it is absent, the layer is
    /// unprojected, or the two already agree.
    /// </summary>
    internal static IGeometry? TransformQueryGeometry(
        IGeometry? geometry,
        CoordinateReference? layerCrs,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        if (geometry is null || layerCrs is null || geometry.CoordinateReference is not { } source || source == layerCrs)
        {
            return geometry;
        }

        return transforms.Transform(geometry, source.ToString(), layerCrs.Value.ToString(), cancellationToken);
    }

    /// <summary>What a match geometry needs: the parsed query, the layer's CRS and the engine services.</summary>
    internal readonly record struct QueryGeometryRequest(
        EsriFeatureQuery Query,
        CoordinateReference? LayerCrs,
        QueryServices Services);
}
