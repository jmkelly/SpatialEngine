using Spatial.Contracts;
using Spatial.Contracts.Providers;

namespace Spatial.Stores.SqlServer.Core;

/// <summary>
/// Combines the bounding-box and filter predicates of a query into one T-SQL
/// predicate, binding every value as a parameter (ADR-0028). The grammar and
/// SQL rendering live in <see cref="SqlServerFilterParser"/> and
/// <see cref="SqlServerFilterSql"/>; this type only composes them.
/// </summary>
internal static class SqlServerPredicate
{
    public static string? Build(
        DatasetDescription description,
        Spatial.Contracts.BoundingBox? bbox,
        string? filter,
        List<object?> parameters)
    {
        var predicate = BuildBoundingBox(description, bbox, parameters);
        return CombineFilter(description, filter, parameters, predicate);
    }

    private static string? BuildBoundingBox(
        DatasetDescription description,
        Spatial.Contracts.BoundingBox? bbox,
        List<object?> parameters)
    {
        if (bbox is null)
        {
            return null;
        }

        var bounds = new BoundingBox(bbox.MinX, bbox.MinY, bbox.MaxX, bbox.MaxY);
        return SqlServerFilterSql.BoundingBox(bounds, description.GeometryColumn, description.Srid, parameters);
    }

    private static string? CombineFilter(
        DatasetDescription description,
        string? filter,
        List<object?> parameters,
        string? predicate)
    {
        if (filter is null)
        {
            return predicate;
        }

        return Join(predicate, BuildFilter(description, filter, parameters));
    }

    private static string Join(string? predicate, string sql) =>
        predicate is null ? sql : $"({predicate}) AND ({sql})";

    private static string BuildFilter(
        DatasetDescription description,
        string filter,
        List<object?> parameters)
    {
        if (!SqlServerFilterParser.TryParse(filter, out var expression, out var parseError))
        {
            throw SpatialException.BadArguments($"The filter cannot be parsed: {parseError}");
        }

        return Render(description, expression, parameters);
    }

    private static string Render(
        DatasetDescription description, FilterExpression expression, List<object?> parameters)
    {
        if (!SqlServerFilterSql.TryBuild(expression, description.Schema, parameters, out var sql, out var buildError))
        {
            throw SpatialException.BadArguments($"The filter is not supported: {buildError}");
        }

        return sql;
    }
}
