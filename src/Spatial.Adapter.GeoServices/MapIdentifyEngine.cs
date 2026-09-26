using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer <c>identify</c> operation (spec §4.0.5): given a point or
/// envelope, a pixel tolerance and a layer selection, it returns the features
/// that intersect the geometry across the selected layers. The identify
/// geometry is buffered by the tolerance (map units derived from
/// <c>mapExtent</c>/<c>imageDisplay</c>) and matched with the engine's
/// <see cref="IGeometryOperations"/> verbs; attributes and geometry are
/// written as Esri JSON. The <c>time</c>/<c>timeRelation</c>/
/// <c>layerTimeOptions</c> temporal surface (T-059) reuses the export
/// grammar (<see cref="EsriFeatureQuery.ParseTime"/>, <see
/// cref="MapExportTime"/>) and the query-path temporal rule, so dated
/// hits filter exactly as <c>query</c> and <c>export</c> do. The three
/// steps are named apart: <see cref="MapIdentifyPlan"/> reads the request,
/// <see cref="MapIdentifyMatcher"/> matches it, and
/// <see cref="MapIdentifyWriter"/> projects the hits.
/// </summary>
internal static class MapIdentifyEngine
{
    public static async Task<IResult> IdentifyAsync(
        IdentifyRequest request, EsriRequestParameters parameters, CancellationToken cancellationToken)
    {
        var plan = MapIdentifyPlan.Build(request, parameters, cancellationToken);
        var hits = await MapIdentifyMatcher.MatchAsync(new IdentifyServices(request), plan.Layers, plan.Query, cancellationToken);
        return EsriJson.Write(writer => MapIdentifyWriter.WriteResults(writer, hits, plan.Query.ReturnGeometry));
    }
}
