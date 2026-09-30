using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.Contracts;

/// <summary>
/// Validates a feature-read plan, a distinct request and an aggregate request
/// against the dataset's schema, once, where the plan was built
/// (ADR-0074 §1). An unknown projected or sorted field, a filter column the
/// dataset does not have, a duplicate field, a negative cap or a malformed
/// request is a typed <c>invalid.arguments</c> failure at the boundary that
/// received it — never a per-provider surprise discovered after a query has
/// already run.
///
/// <para>
/// The rules are the same for every store, so a plan a store accepts is a
/// plan every store accepts; validation has no provider knowledge and no
/// protocol vocabulary in it. The filter columns are checked here rather than
/// left to each store's compiler, so a plan naming a field no store has is
/// rejected the same way whether it is evaluated in memory or pushed to SQL.
/// </para>
///
/// <para>
/// One statistic is a statistic <em>of</em> a field rather than over its
/// values: the envelope of a geometry field, which reduces geometries and so
/// takes the one field kind no other statistic accepts, and takes nothing else
/// (ADR-0120). It is checked here for the same reason the rest are: a store
/// that met a sum of a geometry column, or an envelope of a numeric one, would
/// have to invent the answer.
/// </para>
/// </summary>
public static class FeatureQueryValidation
{
    /// <summary>Validates a feature-read plan against the dataset's schema.</summary>
    public static void Validate(IFeatureSchema schema, FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(query);

        ValidateFields(schema, query.Projection, "projection");
        ValidateOrder(schema, query.Order);
        ValidateWhere(schema, query.Where);
        ValidatePaging(query);
        ValidateIds(query.Ids);
    }

    /// <summary>
    /// Every field a predicate names, resolved against the schema. A clause on
    /// a field the dataset does not have is a plan no store can answer, and it
    /// is a failure rather than an empty answer — otherwise a typo would read
    /// as "no rows match" on the stores that filter in SQL.
    /// </summary>
    private static void ValidateWhere(IFeatureSchema schema, Predicate? where)
    {
        foreach (var field in where?.Fields() ?? [])
        {
            if (schema.IndexOf(field.Name) < 0)
            {
                throw SpatialException.BadArguments(
                    $"The filter column '{field.Name}' is not a field of this dataset; available fields: "
                    + $"{string.Join(", ", schema.Fields.Select(known => $"'{known.Name}'"))}.");
            }
        }
    }

    /// <summary>Validates the fields a distinct request deduplicates.</summary>
    public static void ValidateDistinct(IFeatureSchema schema, DistinctQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Fields.Count == 0)
        {
            throw SpatialException.BadArguments("A distinct query must name at least one field.");
        }

        ValidateFields(schema, query.Fields, "distinct");
    }

    /// <summary>
    /// Validates a grouped reduction against the dataset's schema: the group
    /// fields, the statistics, the <c>having</c> clause's vocabulary and the
    /// page over the groups.
    /// </summary>
    public static void ValidateAggregate(IFeatureSchema schema, AggregateQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Specs.Count == 0)
        {
            throw SpatialException.BadArguments("An aggregate query must request at least one statistic.");
        }

        ValidateFields(schema, query.GroupBy, "grouping", allowAll: false);

        for (var i = 0; i < query.Specs.Count; i++)
        {
            ValidateSpec(schema, query.Specs[i], i);
        }

        ValidateHaving(query);
        ValidateGroupPaging(query);
    }

    /// <summary>
    /// Every field a <c>having</c> clause names, resolved against the group row
    /// — the group fields and the statistics' result names, and nothing else
    /// (ADR-0128). The clause is over the <em>reduced</em> group, so a name that
    /// is neither a key nor a result is a clause over a value no group row
    /// carries, and it is rejected here rather than left to a store to answer
    /// as "no group matches".
    /// </summary>
    private static void ValidateHaving(AggregateQuery query)
    {
        var names = query.GroupRowFields;
        foreach (var field in query.Having?.Fields() ?? [])
        {
            if (!names.Contains(field.Name, StringComparer.Ordinal))
            {
                throw SpatialException.BadArguments(
                    $"The 'having' column '{field.Name}' is neither a group field nor a statistic of this reduction; "
                    + $"available: {string.Join(", ", names.Select(name => $"'{name}'"))}.");
            }
        }
    }

    /// <summary>
    /// The page over the groups: a non-negative start and a positive cap. A
    /// cap of zero is not a page that returns nothing — it is a cap no store
    /// can distinguish from "no cap", so it is a bad argument rather than an
    /// empty answer, exactly as a plan's cap is.
    /// </summary>
    private static void ValidateGroupPaging(AggregateQuery query)
    {
        if (query.Limit is <= 0)
        {
            throw SpatialException.BadArguments($"An aggregate query's group limit must be positive, got {query.Limit}.");
        }

        if (query.Offset is < 0)
        {
            throw SpatialException.BadArguments($"An aggregate query's group offset must not be negative, got {query.Offset}.");
        }
    }

    private static void ValidateSpec(IFeatureSchema schema, AggregateSpec spec, int position)
    {
        var label = $"statistic {position}";
        if (spec.Field == AggregateSpec.AllFields)
        {
            if (spec.Statistic != AggregateStatistic.Count)
            {
                throw SpatialException.BadArguments(
                    $"Aggregate {label} '{spec.Name}': only a count may reduce every row with '{AggregateSpec.AllFields}'.");
            }

            return;
        }

        var index = Require(schema, spec.Field, label);
        var kind = schema[index].Kind;
        if (IsEnvelope(spec.Statistic))
        {
            if (kind != AttributeKind.Geometry)
            {
                throw SpatialException.BadArguments(
                    $"Aggregate {label} '{spec.Name}': an envelope needs a geometry field, but '{spec.Field}' is {kind}.");
            }

            return;
        }

        if (kind == AttributeKind.Geometry)
        {
            throw SpatialException.BadArguments(
                $"Aggregate {label} '{spec.Name}': a geometry field cannot be aggregated.");
        }

        if (IsNumeric(spec.Statistic) && kind is not (AttributeKind.Int64 or AttributeKind.Double))
        {
            throw SpatialException.BadArguments(
                $"Aggregate {label} '{spec.Name}': {spec.Statistic} needs a numeric field, but '{spec.Field}' is {kind}.");
        }

        if (IsPercentile(spec.Statistic)
            && (spec.Percentile is < 0 or > 1 || double.IsNaN(spec.PercentileFraction)))
        {
            throw SpatialException.BadArguments(
                $"Aggregate {label} '{spec.Name}': a percentile fraction must be between 0 and 1.");
        }
    }

    private static void ValidateOrder(IFeatureSchema schema, IReadOnlyList<OrderTerm>? order)
    {
        if (order is null)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < order.Count; i++)
        {
            var field = order[i].Field;
            var index = Require(schema, field, $"sort key {i}");
            if (schema[index].Kind == AttributeKind.Geometry)
            {
                throw SpatialException.BadArguments(
                    $"Sort key {i}: a geometry field ('{field}') has no value ordering.");
            }

            if (!seen.Add(field))
            {
                throw SpatialException.BadArguments($"Sort key {i}: '{field}' is named twice.");
            }
        }
    }

    private static void ValidatePaging(FeatureQuery query)
    {
        if (query.Limit is { } limit && limit < 0)
        {
            throw SpatialException.BadArguments($"A row cap cannot be negative, got {limit}.");
        }

        if (query.Offset is { } offset && offset < 0)
        {
            throw SpatialException.BadArguments($"A page start cannot be negative, got {offset}.");
        }

        if (query.Cursor is not null && query.Offset is not null)
        {
            throw SpatialException.BadArguments(
                "A plan carries either a cursor or an offset, never both: a cursor is the page start it was issued for.");
        }
    }

    private static void ValidateIds(IReadOnlyList<FeatureId>? ids)
    {
        if (ids?.Any(id => string.IsNullOrWhiteSpace(id.Value)) == true)
        {
            throw SpatialException.BadArguments("A plan's identities cannot contain a blank value.");
        }
    }

    private static void ValidateFields(IFeatureSchema schema, IReadOnlyList<string>? fields, string label, bool allowAll = true)
    {
        if (fields is null)
        {
            return;
        }

        if (fields.Count == 0)
        {
            throw SpatialException.BadArguments(
                $"An empty {label} names no field; omit it instead (null means every field).");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            if (allowAll && field == AggregateSpec.AllFields && schema.Count > 0)
            {
                continue;
            }

            Require(schema, field, $"{label} field {i}");
            if (!seen.Add(field))
            {
                throw SpatialException.BadArguments($"The {label} names '{field}' twice.");
            }
        }
    }

    private static int Require(IFeatureSchema schema, string field, string label)
    {
        var index = string.IsNullOrWhiteSpace(field) ? -1 : schema.IndexOf(field);
        return index >= 0
            ? index
            : throw SpatialException.BadArguments(
                $"The {label} names unknown field '{field}'. Known fields: {string.Join(", ", schema.Fields.Select(known => known.Name))}.");
    }

    private static bool IsNumeric(AggregateStatistic statistic) =>
        statistic is AggregateStatistic.Sum or AggregateStatistic.Average or AggregateStatistic.Variance
            or AggregateStatistic.StdDev or AggregateStatistic.PercentileContinuous or AggregateStatistic.PercentileDiscrete;

    private static bool IsEnvelope(AggregateStatistic statistic) => statistic == AggregateStatistic.Envelope;

    private static bool IsPercentile(AggregateStatistic statistic) =>
        statistic is AggregateStatistic.PercentileContinuous or AggregateStatistic.PercentileDiscrete;
}
