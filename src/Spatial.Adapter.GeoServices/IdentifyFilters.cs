using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// One identify layer's attribute and temporal filters: the <c>layerDefs</c>
/// definition expression (with the synthetic <c>OBJECTID</c> resolved as the
/// query path does) and the layer's time extent. Geometry intersection stays
/// with <see cref="MapIdentifyMatcher"/>; parsing a <c>layerDefs</c> clause
/// shares the query path's safe grammar, so client text never becomes SQL
/// structure.
/// </summary>
internal static class IdentifyFilters
{
    /// <summary>
    /// Applies one layer's attribute and temporal filters to a feature:
    /// the <c>layerDefs</c> definition expression and the layer's time
    /// extent.
    /// </summary>
    public static bool MatchesFilters(
        EsriObjectIdScheme? scheme,
        DatasetDescription dataset,
        EsriFilterClause? definition,
        Feature feature,
        long ordinal,
        MapTimeExtent? extent)
    {
        if (definition is not null && !MatchesDefinition(scheme!, dataset, definition, feature, ordinal))
        {
            return false;
        }

        if (extent is not null
            && !FeatureSpatialMatcher.MatchesTime(feature, new EsriTimeExtent(extent.StartMs, extent.EndMs)))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parses one layer's <c>layerDefs</c> clause. The clause was validated
    /// when the <c>layerDefs</c> object parsed, so its re-rendered form always
    /// parses; a failure here is still typed rather than silent.
    /// </summary>
    public static EsriFilterClause ParseLayerDef(int layerId, string where) =>
        EsriFilterClause.TryParse(where, out var clause, out var error) && clause is not null
            ? clause
            : throw GeoServicesErrors.Invalid($"'layerDefs' clause for layer {layerId} is not supported: {error}.");

    /// <summary>
    /// Applies one layer's definition to a feature, resolving the synthetic
    /// <c>OBJECTID</c> exactly as the query path does.
    /// </summary>
    public static bool MatchesDefinition(
        EsriObjectIdScheme scheme, DatasetDescription dataset, EsriFilterClause definition, Feature feature, long ordinal)
    {
        if (!scheme.TryResolve(feature, ordinal, out var objectId))
        {
            throw GeoServicesErrors.ServerError(
                $"The identity column of layer '{dataset.Id}' is not an integer.");
        }

        return definition.Matches(feature, new EsriSyntheticField(EsriLayerModel.ObjectIdField, AttributeValue.FromInt64(objectId)));
    }
}
