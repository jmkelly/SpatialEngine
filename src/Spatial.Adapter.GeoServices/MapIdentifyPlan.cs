using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer <c>identify</c> request plan (spec §4.0.5): the layer
/// selection, the per-layer <c>layerDefs</c> filters, the temporal selection
/// and the query geometry the tolerance buffered, all read from the Esri
/// parameters. Split out of <see cref="MapIdentifyEngine"/> — which only
/// runs the plan and writes the result — so the projection chain the query,
/// export and identify paths share is named once.
/// </summary>
internal sealed record MapIdentifyPlan(IReadOnlyList<MapLayerInfo> Layers, IdentifyQuery Query)
{
    /// <summary>Plans one identify: the selected layers and the buffered query geometry they are matched against.</summary>
    public static MapIdentifyPlan Build(IdentifyRequest request, EsriRequestParameters parameters, CancellationToken cancellationToken)
    {
        var identifyCrs = EsriValueParser.ParseSpatialReference(parameters.Get("sr"))
            ?? EsriLayerModel.LayerCoordinateReference(request.MapSrid);
        var geometry = EsriValueParser.ParseGeometry(parameters.Require("geometry"), identifyCrs);
        var tolerance = IdentifyTolerance.Units(parameters);
        var queryGeometry = tolerance > 0
            ? request.Operations.Buffer(geometry, tolerance, 8, cancellationToken)
            : geometry;
        var selected = MapLayerSelection.Select(request.Layers, parameters.Get("layers"));
        var relation = MapExportTime.ParseTimeRelation(parameters.Get("timeRelation"));
        MapExportTime.RequireDesignated(relation ?? TemporalRelation.Overlaps,
            [.. selected.Select(layer => new MapDesignation(layer.Layer.Id, layer.Layer.Name, layer.Dataset.TimeFields))]);
        var times = MapExportTime.ResolveTimes(
            [.. selected.Select(layer => layer.Layer)],
            EsriFeatureQuery.ParseTime(parameters.Get("time")),
            MapExportTime.ParseLayerTimeOptions(parameters.Get("layerTimeOptions")),
            relation);
        return new MapIdentifyPlan(
            selected,
            new IdentifyQuery(
                MapRenderParameters.ParseLayerDefs(parameters.Get("layerDefs")),
                times,
                queryGeometry,
                identifyCrs,
                parameters.GetBool("returnGeometry", true)));
    }
}
