using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The related layer's own query, as <c>queryRelatedRecords</c> parses it
/// (ADR-0077): the layer's <c>where</c>, field projection, geometry,
/// <c>spatialRel</c> and <c>outSR</c>, read through the same parser the
/// per-layer <c>query</c> uses so the two agree on what a filter means. The
/// parameters that address the <em>origin</em> layer
/// (<c>objectIds</c>, <c>relationshipId</c>) are dropped before parsing,
/// because on this resource they are not the related layer's.
/// </summary>
internal sealed record RelatedQuery(EsriFeatureQuery Query, CoordinateReference? LayerCrs)
{
    /// <summary>Parses the related layer's query and rejects the result shapes that do not apply to a traversal.</summary>
    public static RelatedQuery Parse(EsriRequestParameters parameters, DatasetDescription related)
    {
        var query = EsriFeatureQuery.Parse(parameters.Without("objectIds", "relationshipId"), EsriLayerModel.LayerCoordinateReference(related.Srid));
        RejectUnsupportedShapes(related, query);
        return new RelatedQuery(query, EsriLayerModel.LayerCoordinateReference(related.Srid));
    }

    /// <summary>
    /// A traversal answers rows per origin record, so the shapes that count,
    /// list ids or aggregate a single flat feature set have no meaning here.
    /// Each is rejected by name with the operation that does serve it rather
    /// than silently ignored (the T-024 silent-ignore rule).
    /// </summary>
    private static void RejectUnsupportedShapes(DatasetDescription related, EsriFeatureQuery query)
    {
        if (query.ReturnIdsOnly)
        {
            throw Unsupported(related, "returnIdsOnly", "a traversal returns the related records, not their ids.");
        }

        if (query.ReturnCountOnly)
        {
            throw Unsupported(related, "returnCountOnly", "count the related records per 'objectIds' value on the served rows.");
        }

        if (query.ReturnExtentOnly)
        {
            throw Unsupported(related, "returnExtentOnly", "use 'query' on the related layer to get its extent.");
        }

        if (query.ReturnDistinctValues)
        {
            throw Unsupported(related, "returnDistinctValues", "use 'query' on the related layer with 'returnDistinctValues'.");
        }

        if (query.OutStatistics is not null)
        {
            throw Unsupported(related, "outStatistics", "use 'query' on the related layer with 'outStatistics'.");
        }

        if (query.UniqueIds is not null)
        {
            throw Unsupported(related, "uniqueIds", "use 'objectIds' on 'queryRelatedRecords' to address origin records.");
        }

        if (query.ResultPaginationToken is not null || query.ResultOffset is not null || query.ResultRecordCount is not null)
        {
            throw Unsupported(
                related,
                "resultOffset/resultRecordCount",
                "a traversal returns every related record of each origin record, so it is not paged.");
        }
    }

    private static EsriInteropException Unsupported(DatasetDescription related, string parameter, string guidance) =>
        GeoServicesErrors.Invalid(
            $"The '{parameter}' parameter is not supported on 'queryRelatedRecords' for layer '{related.Id}': {guidance}");
}
