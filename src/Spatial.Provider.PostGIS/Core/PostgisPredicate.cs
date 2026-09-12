using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.PostGIS.Core;

/// <summary>
/// Combines the bounding-box and filter predicates of a query into one SQL
/// predicate, binding every value as a parameter (ADR-0028). The grammar and
/// SQL rendering live in <see cref="PostgisFilterParser"/> and
/// <see cref="PostgisFilterSql"/>; this type only composes them.
/// </summary>
internal static class PostgisPredicate
{
    public static string? Build(
        DatasetDescription description, Spatial.PluginSdk.BoundingBox? bbox, string? filter, List<object?> parameters)
    {
        string? predicate = null;
        if (bbox is not null)
        {
            var bounds = new BoundingBox(bbox.MinX, bbox.MinY, bbox.MaxX, bbox.MaxY);
            predicate = PostgisFilterSql.BoundingBox(bounds, description.GeometryColumn, description.Srid, parameters);
        }

        if (filter is not null)
        {
            if (!PostgisFilterParser.TryParse(filter, out var expression, out var parseError))
            {
                throw SpatialException.BadArguments($"The filter cannot be parsed: {parseError}");
            }

            if (!PostgisFilterSql.TryBuild(expression, description.Schema, parameters, out var sql, out var buildError))
            {
                throw SpatialException.BadArguments($"The filter is not supported: {buildError}");
            }

            predicate = predicate is null ? sql : $"({predicate}) AND ({sql})";
        }

        return predicate;
    }
}
