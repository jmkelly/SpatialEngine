using System.Text;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Querying;

namespace Spatial.Stores.PostGIS.Core;

/// <summary>
/// The SQL of the feature-read plan and the reductions over it (ADR-0074
/// §4, §6): one projection, one order, one row cap, one count and one grouped
/// aggregate. Every identifier in the text is a discovered column or the
/// dataset's own qualified name, and every literal is a bound parameter
/// (ADR-0028), so no client text reaches SQL.
///
/// <para>
/// Four dialect facts are the whole reason this file exists, because each one
/// is a place where a naive pushdown would answer a <em>different</em> question
/// from the reference: Postgres sorts nulls last ascending (which is the
/// contract's rule) and first descending, so each term is written out
/// explicitly rather than inherited; <c>SUM</c>/<c>AVG</c>/<c>VAR_SAMP</c>
/// return <c>numeric</c>, so a reduced value is coerced back to the kind the
/// reference reports; <c>GROUP BY</c> returns rows in no defined order, so a
/// grouped reduction is only pushed down when the plan asks for an order over
/// the group key itself (a group order SQL can return) — otherwise the rows are
/// reduced here, where first-seen order is knowable; a text sort key
/// inherits the <em>database's</em> collation, which is a locale comparison
/// where the contract's is a byte one, so a term over a text column carries
/// <c>COLLATE "C"</c> unless the database already compares by bytes
/// (ADR-0121); and <c>ST_Extent</c>
/// answers a <c>box2d</c>, so the rectangle is cast back to a geometry and read
/// through the one geometry reader this provider has (ADR-0120).
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
    /// statistic, grouped over the key, filtered by the clause over the reduced
    /// groups, ordered by the plan's order with the group key appended so the
    /// row order is total, and cut to the page (ADR-0128).
    ///
    /// <para>
    /// The clause and the page are the grouped statement's own <c>HAVING</c> and
    /// <c>LIMIT</c>, not a wrap of it: <c>GROUP BY</c>, <c>HAVING</c>,
    /// <c>ORDER BY</c> and <c>LIMIT</c> compose in that order, so the cap cuts
    /// the groups the clause kept and the server assembles no group past the
    /// page. A name the clause uses is a group column (the key's own column, so
    /// the filter is a comparison under the byte order, ADR-0123) or a result
    /// name, which is not a column at all and is written as the dialect's own
    /// aggregate spelling — the expression the select list already carries.
    /// </para>
    ///
    /// <para>
    /// Returns <c>null</c> when a <em>grouped</em> reduction has no order the
    /// store can return: a group order the plan did not ask for is not this
    /// store's to invent (<c>GROUP BY</c> returns rows in no defined order,
    /// while the contract's order for a reduction is the plan's), and an order
    /// naming a column the group key does not carry is a different question
    /// again — grouping by it too would return more groups than the reference
    /// does, and ordering a group by one of its own aggregates is not a
    /// <c>GROUP BY</c> order at all. A clause naming a value the group row does
    /// not carry is declined the same way. The caller then reduces the rows it
    /// read instead, where first-seen order is knowable. An <em>ungrouped</em>
    /// reduction is one group whatever the order, so it is always one aggregate
    /// row.
    /// </para>
    /// </summary>
    public static string? Aggregate(
        PostgisDatasetName dataset,
        string? where,
        IReadOnlyList<string> groupColumns,
        IReadOnlyList<AggregateSpec> specs,
        IReadOnlyList<OrderTerm> order,
        IFeatureSchema schema,
        bool byteOrderText,
        List<object?> parameters,
        Predicate? having = null,
        Paging? paging = null)
    {
        if (groupColumns.Count > 0 && (order.Count == 0 || order.Any(term => !groupColumns.Contains(term.Field, StringComparer.Ordinal))))
        {
            return null;
        }

        var terms = order.Select(term => Term(term, schema, byteOrderText)).ToList();
        terms.AddRange(groupColumns.Select(column => Ascending(column, schema, byteOrderText)));
        var selects = groupColumns.Select(Quote).ToList();
        var statistics = specs.Select(spec => new Statistic(spec, schema, byteOrderText)).ToArray();
        selects.AddRange(statistics.Select(statistic => statistic.Expression(parameters)));
        var builder = new StringBuilder("SELECT ")
            .Append(string.Join(", ", selects))
            .Append(" FROM ")
            .Append(dataset.QuoteQualified());
        AppendWhere(builder, where);
        if (groupColumns.Count > 0)
        {
            builder.Append(" GROUP BY ").Append(string.Join(", ", groupColumns.Select(Quote)));
        }

        if (having is not null)
        {
            var clause = Having(having, groupColumns, specs, schema, byteOrderText, parameters);
            if (clause is null)
            {
                return null;
            }

            builder.Append(" HAVING ").Append(clause);
        }

        if (terms.Count > 0)
        {
            builder.Append(" ORDER BY ").Append(string.Join(", ", terms));
        }

        (paging ?? new Paging(null, 0)).AppendTo(builder, parameters);
        return builder.ToString();
    }

    /// <summary>
    /// The <c>HAVING</c> fragment for a clause over the reduced groups, or
    /// <c>null</c> when it names a value the group row does not carry — a name
    /// that is neither a group key nor a requested statistic, which the caller
    /// then answers with the reference over the rows it read.
    ///
    /// <para>
    /// It is the predicate compiler over a different resolution: a group column
    /// resolves to the column (under the byte order every string comparison in
    /// this store states) and a result name to the statistic's own aggregate
    /// expression, rebuilt here exactly as the select list builds it. The tree,
    /// the operators, the literal binding and the three-valued reading of a
    /// null are the compiler's, so a pushed <c>HAVING</c> cannot be a second
    /// definition of what a comparison means.
    /// </para>
    /// </summary>
    private static string? Having(
        Predicate having,
        IReadOnlyList<string> groupColumns,
        IReadOnlyList<AggregateSpec> specs,
        IFeatureSchema schema,
        bool byteOrderText,
        List<object?> parameters)
    {
        var expressions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in groupColumns)
        {
            expressions[column] = Ordered(column, schema, byteOrderText);
        }

        for (var i = 0; i < specs.Count; i++)
        {
            expressions[specs[i].Name] = new Statistic(specs[i], schema, byteOrderText).Expression(parameters);
        }

        return PostgisPredicateSql.Having(
            having,
            field => expressions.TryGetValue(field.Name, out var expression)
                ? new PostgisPredicateSql.GroupColumn(expression, GroupKind(specs, groupColumns, field.Name, schema))
                : null,
            parameters);
    }

    /// <summary>
    /// The kind a group row's value compares as: a group column keeps the
    /// column's own kind, and a result is the kind the reference reports it as
    /// (<see cref="FeatureReduction.ResultKind"/>) — the same rule the row
    /// mapper applies, so a pushed clause binds a literal the same way the
    /// reduction's own values are read back.
    /// </summary>
    private static AttributeKind GroupKind(
        IReadOnlyList<AggregateSpec> specs, IReadOnlyList<string> groupColumns, string name, IFeatureSchema schema)
    {
        var group = groupColumns.ToList().FindIndex(column => string.Equals(column, name, StringComparison.Ordinal));
        if (group >= 0)
        {
            return schema[schema.IndexOf(groupColumns[group])].Kind;
        }

        var spec = specs.First(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        return FeatureReduction.ResultKind(spec, spec.IsRowCount ? AttributeKind.Int64 : schema[schema.IndexOf(spec.Field)].Kind);
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
    /// placement (nulls last ascending, first descending — the contract's rule),
    /// each text term under the byte-order collation the contract compares
    /// strings in (ADR-0121), and then the dataset's identity columns as the
    /// contract's mandatory tie-break, so the total order is deterministic and a
    /// page boundary can never fall between two rows the next page would
    /// re-order. When the table has no identity there is no tie-break to append,
    /// and this returns <c>null</c> so the caller orders the rows it read with
    /// the reference executor instead.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string>? Order(
        IReadOnlyList<OrderTerm> order, IReadOnlyList<string> identityColumns, IFeatureSchema schema, bool byteOrderText)
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
            terms.Add(Term(term, schema, byteOrderText));
        }

        terms.AddRange(identityColumns.Select(column => Ascending(column, schema, byteOrderText)));
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
    /// <paramref name="byteOrderText"/> is the database's own answer to whether
    /// it already compares text by bytes (ADR-0121), and it decides the
    /// collation every string comparison in the restriction states — the
    /// attribute clause's (ADR-0123) and the identity restriction's
    /// (ADR-0126), which is the same question asked of the same property. The
    /// caller reads it from the database — once per store, cached — and only
    /// when the restriction actually compares text.
    /// </para>
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
        PostgisDatasetName dataset,
        DatasetDescription description,
        FeatureQuery query,
        bool byteOrderText,
        List<object?> parameters)
    {
        if (description.IdColumns.Count == 0 && Restricts(query))
        {
            return null;
        }

        var identity = Identity(description, query.Ids, byteOrderText, parameters);
        var box = BoundingBox(description, query.BoundingBox, parameters);
        var clause = query.Where is { } where
            ? PostgisPredicateSql.Where(where, description.Schema, byteOrderText, parameters)
            : null;
        return Join(identity, Join(box, clause));
    }

    /// <summary>Whether the plan asks for anything other than every row.</summary>
    public static bool Restricts(FeatureQuery query) =>
        query.Ids is not null || query.BoundingBox is not null || query.Where is not null;

    private static string? Identity(
        DatasetDescription description, IReadOnlyList<FeatureId>? ids, bool byteOrderText, List<object?> parameters)
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

        var groups = new List<string>(ids.Count);
        foreach (var id in ids)
        {
            var values = PostgisIdentity.Values(description, id);
            var terms = description.IdColumns.Select((column, position) =>
                $"{PostgisIdentity.Operand(description.Schema, column, byteOrderText)} = {Parameter(parameters, values[position])}");
            groups.Add(string.Join(" AND ", terms));
        }

        return groups.Count == 1 ? groups[0] : "(" + string.Join(" OR ", groups) + ")";
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

    /// <summary>
    /// The deduplicated field combinations a plan selects, as a
    /// <c>SELECT DISTINCT</c> and the order they are reported in — or
    /// <c>null</c> when this dialect cannot state that order (ADR-0133 §6).
    ///
    /// <para>
    /// A <c>DISTINCT</c> is pushed for the plan whose order is <em>total over
    /// the distinct rows</em>: every term of the plan's order names a requested
    /// field, and the terms between them cover every requested field. That is the
    /// plan whose first-seen order — what the reference reports — is the order
    /// of the rows themselves. Any other plan leaves distinct rows tying on the
    /// order's terms, and a dialect says nothing about which of those comes
    /// first, so the set is reduced over the read instead, where the first-seen
    /// order is knowable.
    /// </para>
    ///
    /// <para>
    /// The order is written on an outer statement over the <c>DISTINCT</c>,
    /// because Postgres takes an <c>ORDER BY</c> over a <c>DISTINCT</c> only
    /// from its own select list and the reference's null-placement key is not
    /// one of the requested fields. A text field is deduplicated and ordered
    /// under the byte-order collation, since a locale collation folds
    /// <c>"A"</c> onto <c>"a"</c> and the reference counts them as two values
    /// (ADR-0121). A geometry is not pushed at all: the dialect deduplicates it
    /// by its stored bytes and the reference by its value.
    /// </para>
    /// </summary>
    public static string? Distinct(
        PostgisDatasetName dataset,
        IReadOnlyList<string> fields,
        IReadOnlyList<OrderTerm> order,
        IFeatureSchema schema,
        bool byteOrderText,
        string? where,
        List<object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (fields.Count == 0 || order.Count == 0 || !Total(fields, order))
        {
            return null;
        }

        if (fields.Any(field => schema.IndexOf(field) < 0 || schema[schema.IndexOf(field)].Kind == AttributeKind.Geometry))
        {
            return null;
        }

        var builder = new StringBuilder("SELECT * FROM (SELECT DISTINCT ")
            .Append(string.Join(", ", fields.Select(field => Alias(Ordered(field, schema, byteOrderText), field))))
            .Append(" FROM ")
            .Append(dataset.QuoteQualified());
        AppendWhere(builder, where);
        return builder.Append(") AS d ORDER BY ")
            .Append(string.Join(", ", order.Select(term => Term(term, schema, byteOrderText, "d"))))
            .ToString();
    }

    /// <summary>
    /// Whether the plan's order names the deduplicated fields and covers them
    /// between its terms, which is what makes it a total order over the distinct
    /// rows (ADR-0133 §6).
    /// </summary>
    private static bool Total(IReadOnlyList<string> fields, IReadOnlyList<OrderTerm> order)
    {
        var named = new HashSet<string>(order.Select(term => term.Field), StringComparer.Ordinal);
        return order.All(term => fields.Contains(term.Field, StringComparer.Ordinal)) && fields.All(named.Contains);
    }

    /// <summary>
    /// A deduplicated column named for the field it came from, so the derived row
    /// is read by the plan's own field names — for the expression that is not
    /// already spelled as the column.
    /// </summary>
    private static string Alias(string expression, string field) =>
        expression == Quote(field) ? expression : $"{expression} AS {Quote(field)}";

    /// <summary>One aggregate expression, with its bound parameters, in the plan's result order.</summary>
    private sealed class Statistic(AggregateSpec spec, IFeatureSchema schema, bool byteOrderText)
    {
        public string Expression(List<object?> parameters) => spec.Statistic switch
        {
            AggregateStatistic.Count when spec.IsRowCount => "COUNT(*)",
            AggregateStatistic.Count => $"COUNT({Quote(spec.Field)})",
            AggregateStatistic.Sum => $"SUM({Quote(spec.Field)})",
            AggregateStatistic.Average => $"AVG({Quote(spec.Field)})",
            AggregateStatistic.Variance => $"VAR_SAMP({Quote(spec.Field)})",
            AggregateStatistic.StdDev => $"STDDEV_SAMP({Quote(spec.Field)})",
            AggregateStatistic.Minimum => $"MIN({Ordered(spec.Field, schema, byteOrderText)})",
            AggregateStatistic.Maximum => $"MAX({Ordered(spec.Field, schema, byteOrderText)})",
            AggregateStatistic.Envelope => Extent(),
            _ => Percentile(spec, parameters),
        };

        /// <summary>
        /// The bounding rectangle of a geometry column as one value the
        /// provider's own EWKB reader can read back, so a pushed-down extent is
        /// mapped exactly like a stored geometry rather than through a second,
        /// box-shaped reader. <c>ST_Extent</c> answers a <c>box2d</c> and casts
        /// to a <c>POLYGON</c> whose envelope is that box; over a group whose
        /// geometries are all null it answers <c>NULL</c>, which is the
        /// reference's answer for a group with nothing to reduce.
        /// </summary>
        private string Extent() => $"ST_AsEWKB(ST_Extent({Quote(spec.Field)})::geometry)";

        /// <summary>
        /// A percentile as an ordered-set aggregate over the field, ranked
        /// ascending or descending as the statistic asked, with the fraction
        /// bound as a parameter rather than written into the text. The
        /// <c>ORDER BY</c> is a sort key like any other, so a text field is
        /// ranked under the same byte-order collation (ADR-0121).
        /// </summary>
        private string Percentile(AggregateSpec spec, List<object?> parameters)
        {
            var name = spec.Statistic == AggregateStatistic.PercentileContinuous ? "PERCENTILE_CONT" : "PERCENTILE_DISC";
            var direction = spec.PercentileDescending ? "DESC" : "ASC";
            var key = SortKey(spec.Field, direction, schema, byteOrderText);
            return $"{name}({Parameter(parameters, spec.PercentileFraction)}) WITHIN GROUP (ORDER BY {key})";
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

    private static string Ascending(string column, IFeatureSchema schema, bool byteOrderText) =>
        SortKey(column, "ASC NULLS LAST", schema, byteOrderText);

    /// <summary>One requested sort key as SQL, with its explicit null placement.</summary>
    private static string Term(OrderTerm term, IFeatureSchema schema, bool byteOrderText, string qualifier = "") =>
        term.IsDescending
            ? SortKey(term.Field, "DESC NULLS FIRST", schema, byteOrderText, qualifier)
            : SortKey(term.Field, "ASC NULLS LAST", schema, byteOrderText, qualifier);

    /// <summary>One sort key as SQL: the ordered column, then the direction and
    /// the explicit null placement.</summary>
    private static string SortKey(string column, string direction, IFeatureSchema schema, bool byteOrderText, string qualifier = "") =>
        $"{Ordered(column, schema, byteOrderText, qualifier)} {direction}";

    /// <summary>
    /// A column as an expression that compares text by bytes: the quoted column,
    /// and — for a <em>text</em> column in a database that does not already
    /// compare by bytes — <c>COLLATE "C"</c>.
    ///
    /// <para>
    /// Every string comparison this store writes goes through here, because
    /// they are one question: an <c>ORDER BY</c> over a text sort key, the
    /// tie-break a page boundary is cut on, a <c>GROUP BY</c>'s group order, a
    /// <c>MIN</c>/<c>MAX</c> over a text column and a pushed <c>WHERE</c>'s own
    /// comparison all inherit the database's collation, and the contract's
    /// answer is a byte comparison (ADR-0098 §3, ADR-0121, ADR-0123). A
    /// numeric, date-time or boolean column takes no collation: <c>COLLATE</c>
    /// is a string operator, and applying it to a column of another kind is a
    /// statement Postgres refuses.
    /// </para>
    /// </summary>
    internal static string Ordered(string column, IFeatureSchema schema, bool byteOrderText, string qualifier = "")
    {
        var quoted = qualifier.Length == 0 ? Quote(column) : $"{Quote(qualifier)}.{Quote(column)}";
        var index = schema.IndexOf(column);
        var text = index >= 0 && schema[index].Kind == AttributeKind.String;
        return !text || byteOrderText ? quoted : $"{quoted} COLLATE {PostgisTextCollation.ByteOrder}";
    }

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
