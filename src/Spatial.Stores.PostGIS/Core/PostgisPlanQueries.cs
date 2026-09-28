using System.Text;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.Stores.PostGIS.Core;

/// <summary>
/// The SQL of the feature-read plan and the reductions over it (ADR-0074
/// §4, §6): one projection, one order, one row cap, one count and one grouped
/// aggregate. Every identifier in the text is a discovered column or the
/// dataset's own qualified name, and every literal is a bound parameter
/// (ADR-0028), so no client text reaches SQL.
///
/// <para>
/// Three dialect facts are the whole reason this file exists, because each one
/// is a place where a naive pushdown would answer a <em>different</em> question
/// from the reference: Postgres sorts nulls last ascending (which is the
/// contract's rule) and first descending, so each term is written out
/// explicitly rather than inherited; <c>SUM</c>/<c>AVG</c>/<c>VAR_SAMP</c>
/// return <c>numeric</c>, so a reduced value is coerced back to the kind the
/// reference reports; and <c>GROUP BY</c> returns rows in no defined order, so a
/// grouped reduction is only pushed down when the plan asks for an order (a
/// group order SQL can express) — otherwise the rows are reduced here, where
/// first-seen order is knowable.
/// </para>
/// </summary>
internal static class PostgisPlanQueries
{
    /// <summary>
    /// The plan read: the projected columns of the rows the plan selects, in
    /// the plan's order, capped. <paramref name="paging"/> carries the row cap
    /// and the page start as bound values.
    /// </summary>
    public static string Read(
        PostgisDatasetName dataset,
        IReadOnlyList<string> columns,
        string? where,
        IReadOnlyList<string>? order,
        Paging paging,
        List<object?> parameters)
    {
        var builder = new StringBuilder("SELECT ")
            .Append(string.Join(", ", columns))
            .Append(" FROM ")
            .Append(dataset.QuoteQualified());
        AppendWhere(builder, where);
        AppendOrder(builder, order);
        paging.AppendTo(builder, parameters);
        return builder.ToString();
    }

    /// <summary>The count of the rows the plan selects: one aggregate row, no columns.</summary>
    public static string Count(PostgisDatasetName dataset, string? where)
    {
        var builder = new StringBuilder("SELECT COUNT(*) FROM ").Append(dataset.QuoteQualified());
        AppendWhere(builder, where);
        return builder.ToString();
    }

    /// <summary>
    /// The grouped reduction: the group key, one aggregate expression per
    /// statistic, grouped over the key plus every ordered column (an
    /// <c>ORDER BY</c> expression must be grouped), and ordered by the plan's
    /// order with the group key appended so the row order is total. Returns
    /// <c>null</c> when the plan asks for no order: a group order the plan did
    /// not ask for is not this store's to invent, and the caller then reduces
    /// the rows it read instead.
    /// </summary>
    public static string? Aggregate(
        PostgisDatasetName dataset,
        string? where,
        IReadOnlyList<string> groupColumns,
        IReadOnlyList<AggregateSpec> specs,
        IReadOnlyList<OrderTerm> order,
        List<object?> parameters)
    {
        if (order.Count == 0)
        {
            return null;
        }

        var grouped = new List<string>(groupColumns);
        foreach (var field in groupColumns.Concat(order.Select(term => term.Field)))
        {
            if (!grouped.Contains(field, StringComparer.Ordinal))
            {
                grouped.Add(field);
            }
        }

        var terms = order.Select(Term).ToList();
        terms.AddRange(grouped.Select(Ascending));
        var selects = groupColumns.Select(Quote).ToList();
        selects.AddRange(specs.Select(spec => new Statistic(spec).Expression(parameters)));
        var builder = new StringBuilder("SELECT ")
            .Append(string.Join(", ", selects))
            .Append(" FROM ")
            .Append(dataset.QuoteQualified());
        AppendWhere(builder, where);
        builder.Append(" GROUP BY ").Append(string.Join(", ", grouped.Select(Quote)));
        builder.Append(" ORDER BY ").Append(string.Join(", ", terms));
        return builder.ToString();
    }

    /// <summary>
    /// The projection of a plan: the requested fields in the requested order,
    /// or every field when the plan projects nothing. A geometry field is read
    /// as canonical EWKB, exactly as a scan reads it.
    /// </summary>
    public static IReadOnlyList<string> Columns(IFeatureSchema schema, IReadOnlyList<string>? projection)
    {
        var fields = projection is null or { Count: 0 }
            ? schema.Fields
            : projection
                .Where(field => field != AggregateSpec.AllFields)
                .Select(field => schema[schema.IndexOf(field)])
                .ToArray();
        return fields.Select(field => Column(field)).ToArray();
    }
    /// <summary>
    /// The order of a plan as SQL, or <c>null</c> when the plan asked for no
    /// order — an un-ordered read keeps the store's own row order, which is
    /// what a scan returns and therefore what the reference computes over.
    ///
    /// <para>
    /// A plan that does ask for one gets each term with its explicit null
    /// placement (nulls last ascending, first descending — the contract's rule)
    /// and then the dataset's identity columns as the contract's mandatory
    /// tie-break, so the total order is deterministic and a page boundary can
    /// never fall between two rows the next page would re-order. When the table
    /// has no identity there is no tie-break to append, and this returns
    /// <c>null</c> so the caller orders the rows it read with the reference
    /// executor instead.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string>? Order(
        IReadOnlyList<OrderTerm> order, IReadOnlyList<string> identityColumns)
    {
        if (order.Count == 0)
        {
            return null;
        }

        if (identityColumns.Count == 0)
        {
            return null;
        }

        var terms = new List<string>();
        foreach (var term in order)
        {
            terms.Add(Term(term));
        }

        terms.AddRange(identityColumns.Select(Ascending));
        return terms;
    }

    /// <summary>
    /// The restriction a plan pushes into the <c>WHERE</c>: the identity
    /// restriction, the bounding-box pre-filter and the attribute predicate,
    /// all as bound values, all over discovered identifiers. The attribute
    /// clause is compiled by the store's own predicate compiler
    /// (<see cref="PostgisPredicateSql"/>), the same half the scan path uses,
    /// so a plan and a filter cannot drift into two different answers.
    ///
    /// <para>
    /// Returns <c>null</c> when the restriction cannot be expressed without
    /// changing what a feature is. A dataset with no identity column names its
    /// features by the ordinal of the read, so a <c>WHERE</c> that returns only
    /// some of the rows would number those rows 1, 2, 3 — the same feature
    /// would come back with an id that depends on the query, breaking
    /// <c>objectIds</c>, <c>returnIdsOnly</c>, paging and the edit round-trip
    /// (ADR-0097). Such a dataset keeps its restriction in the caller, which
    /// selects over the whole read; so does a dataset with no identity columns
    /// to restrict by identity at all.
    /// </para>
    /// </summary>
    public static string? Predicate(
        PostgisDatasetName dataset, DatasetDescription description, FeatureQuery query, List<object?> parameters)
    {
        if (description.IdColumns.Count == 0 && Restricts(query))
        {
            return null;
        }

        var identity = Identity(description, query.Ids, parameters);
        var box = BoundingBox(description, query.BoundingBox, parameters);
        var clause = query.Where is { } where
            ? PostgisPredicateSql.Where(where, description.Schema, parameters)
            : null;
        return Join(identity, Join(box, clause));
    }

    /// <summary>Whether the plan asks for anything other than every row.</summary>
    private static bool Restricts(FeatureQuery query) =>
        query.Ids is not null || query.BoundingBox is not null || query.Where is not null;

    private static string? Identity(DatasetDescription description, IReadOnlyList<FeatureId>? ids, List<object?> parameters)
    {
        if (ids is null)
        {
            return null;
        }

        if (ids.Count == 0)
        {
            return "FALSE";
        }

        if (description.IdColumns.Count == 0)
        {
            return null;
        }

        var groups = ids
            .Select(id => string.Join(
                " AND ",
                description.IdColumns.Select((column, position) => $"{Quote(column)} = {Parameter(parameters, PostgisIdentity.Values(description, id)[position])}")))
            .ToArray();
        return groups.Length == 1 ? groups[0] : "(" + string.Join(" OR ", groups) + ")";
    }

    /// <summary>
    /// The bounding-box pre-filter as the index-usable envelope overlap the scan
    /// path already uses: the geometry's bounding box must meet the query box.
    /// </summary>
    private static string? BoundingBox(DatasetDescription description, Spatial.Contracts.BoundingBox? box, List<object?> parameters)
    {
        if (box is null)
        {
            return null;
        }

        return $"{Quote(description.GeometryColumn)} && ST_MakeEnvelope("
            + $"{Parameter(parameters, box.MinX)}, {Parameter(parameters, box.MinY)}, "
            + $"{Parameter(parameters, box.MaxX)}, {Parameter(parameters, box.MaxY)}, {description.Srid})";
    }

    private static string? Join(string? left, string? right) =>
        (left, right) switch
        {
            (null, null) => null,
            (null, _) => right,
            (_, null) => left,
            _ => $"({left}) AND ({right})",
        };

    /// <summary>One aggregate expression, with its bound parameters, in the plan's result order.</summary>
    private sealed class Statistic(AggregateSpec spec)
    {
        public string Expression(List<object?> parameters) => spec.Statistic switch
        {
            AggregateStatistic.Count when spec.IsRowCount => "COUNT(*)",
            AggregateStatistic.Count => $"COUNT({Quote(spec.Field)})",
            AggregateStatistic.Sum => $"SUM({Quote(spec.Field)})",
            AggregateStatistic.Average => $"AVG({Quote(spec.Field)})",
            AggregateStatistic.Variance => $"VAR_SAMP({Quote(spec.Field)})",
            AggregateStatistic.StdDev => $"STDDEV_SAMP({Quote(spec.Field)})",
            AggregateStatistic.Minimum => $"MIN({Quote(spec.Field)})",
            AggregateStatistic.Maximum => $"MAX({Quote(spec.Field)})",
            _ => Percentile(spec, parameters),
        };

        /// <summary>
        /// A percentile as an ordered-set aggregate over the field, ranked
        /// ascending or descending as the statistic asked, with the fraction
        /// bound as a parameter rather than written into the text.
        /// </summary>
        private static string Percentile(AggregateSpec spec, List<object?> parameters)
        {
            var name = spec.Statistic == AggregateStatistic.PercentileContinuous ? "PERCENTILE_CONT" : "PERCENTILE_DISC";
            var direction = spec.PercentileDescending ? "DESC" : "ASC";
            return $"{name}({Parameter(parameters, spec.PercentileFraction)}) WITHIN GROUP (ORDER BY {Quote(spec.Field)} {direction})";
        }
    }

    private static void AppendWhere(StringBuilder builder, string? where)
    {
        if (where is not null)
        {
            builder.Append(" WHERE ").Append(where);
        }
    }

    private static void AppendOrder(StringBuilder builder, IReadOnlyList<string>? order)
    {
        if (order is not { Count: > 0 })
        {
            return;
        }

        builder.Append(" ORDER BY ").Append(string.Join(", ", order));
    }

    private static string Ascending(string column) => $"{Quote(column)} ASC NULLS LAST";

    /// <summary>One requested sort key as SQL, with its explicit null placement.</summary>
    private static string Term(OrderTerm term) =>
        term.IsDescending ? $"{Quote(term.Field)} DESC NULLS FIRST" : $"{Quote(term.Field)} ASC NULLS LAST";

    /// <summary>One field as a select column: geometry is read as canonical EWKB, everything else by name.</summary>
    private static string Column(FieldDefinition field) =>
        field.Kind == AttributeKind.Geometry ? $"ST_AsEWKB({Quote(field.Name)})" : Quote(field.Name);

    private static string Quote(string column) => $"\"{column}\"";

    private static string Parameter(List<object?> parameters, object? value)
    {
        parameters.Add(value);
        return $"@p{parameters.Count - 1}";
    }

    /// <summary>The row cap and the page start, both bound values.</summary>
    internal readonly record struct Paging(int? Limit, int Offset)
    {
        /// <summary>Whether this page is the whole plan, so neither clause is written.</summary>
        public bool IsWhole => Limit is null && Offset == 0;

        public void AppendTo(StringBuilder builder, List<object?> parameters)
        {
            if (Limit is { } limit)
            {
                builder.Append(" LIMIT ").Append(Parameter(parameters, limit));
            }

            if (Offset > 0)
            {
                if (Limit is null)
                {
                    // Postgres has no OFFSET without LIMIT; -1 is its documented "no limit".
                    builder.Append(" LIMIT -1");
                }

                builder.Append(" OFFSET ").Append(Parameter(parameters, Offset));
            }
        }
    }
}
