using System.Text;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.Stores.SqlServer.Core;

/// <summary>
/// The T-SQL of the feature-read plan's page (ADR-0074 §4-5, ADR-0116 §1,
/// ADR-0124): one order, one page, and the count that says whether more
/// remains. Every identifier in the text is a discovered column or the
/// dataset's own qualified name and every literal is a bound parameter
/// (ADR-0028), so no client text reaches SQL.
///
/// <para>
/// Three dialect facts are the whole reason this file exists, because each one
/// is a place where a naive pushdown answers a <em>different</em> question
/// from the reference (<see cref="Spatial.Querying.AttributeValueComparer"/>):
/// </para>
///
/// <list type="bullet">
/// <item>T-SQL sorts a null as the lowest value there is — first ascending,
/// last descending — and the contract sorts nulls <em>last</em> ascending and
/// first descending, which is the other way round in both directions. Every
/// term therefore leads with a <c>CASE WHEN … IS NULL</c> key that puts the
/// nulls where the contract puts them.</item>
/// <item>A text sort key inherits the column's collation and the collations SQL
/// Server ships by default are a case-insensitive locale comparison, so a
/// text term is read under the code-point collation unless the database
/// already compares that way (ADR-0121, <see cref="SqlServerTextCollation"/>).
/// The term also reads the column through <c>CONVERT(nvarchar(max), …)</c>
/// because <c>text</c>/<c>ntext</c> columns cannot be compared or sorted at
/// all, and a plan must not fail on a table this store did not create.</item>
/// <item><c>OFFSET</c>/<c>FETCH NEXT</c> is legal only over an
/// <c>ORDER BY</c>. A plan whose order this table cannot make total therefore
/// has no page here, and the caller finishes it with the reference executor
/// over the rows it read (<see cref="SqlServerPlanReader.Pushed"/>).</item>
/// </list>
///
/// <para>
/// The order ends with the dataset's identity columns as ascending keys, so
/// the total order is deterministic and a page boundary can never fall between
/// two rows the next page would re-order.</para>
///
/// <para>
/// The reductions are here for the same reason (ADR-0133, ADR-0137): a
/// <c>COUNT</c>, a <c>DISTINCT</c> and a <c>GROUP BY</c> are all things T-SQL
/// has, and each one that inherits a default this contract states the other way
/// round is written out — the null-placement key and the code-point collation
/// again, over a group key and over a deduplicated text value this time. The
/// last statistic T-SQL has no aggregate spelling for, a percentile, is the one
/// written as a window function over the partition a group is.</para>
/// </summary>
internal static class SqlServerPlanQueries
{
    /// <summary>
    /// The alias of the derived table a percentile is ranked over, and the
    /// qualifier every expression above it carries (ADR-0137 §2).
    /// </summary>
    private const char Derived = 'd';

    /// <summary>
    /// The plan read: the projected columns of the rows the plan selects, in
    /// the plan's order, capped.
    /// <paramref name="paging"/> carries the row cap and the page start as
    /// bound values, numbered after whatever the restriction already bound.
    ///
    /// <para>
    /// A page needs an order: T-SQL refuses <c>OFFSET</c>/<c>FETCH NEXT</c>
    /// without an <c>ORDER BY</c>, and a page whose rows are in no order this
    /// statement reproduces is not a position the next one can name. A read
    /// that pages therefore has to arrive with one, and a read that does not
    /// is the whole plan.
    /// </para>
    /// </summary>
    public static string Read(
        SqlServerDatasetName dataset,
        IReadOnlyList<string> columns,
        string? where,
        IReadOnlyList<string>? order,
        Paging paging,
        List<object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!paging.IsWhole && order is not { Count: > 0 })
        {
            throw new ArgumentException(
                "A paged read needs an order: T-SQL takes OFFSET/FETCH NEXT only over an ORDER BY.",
                nameof(order));
        }

        var builder = new StringBuilder("SELECT ")
            .Append(string.Join(", ", columns))
            .Append(" FROM ")
            .Append(dataset.QuoteQualified());
        if (where is not null)
        {
            builder.Append(" WHERE ").Append(where);
        }

        if (order is { Count: > 0 })
        {
            builder.Append(" ORDER BY ").Append(string.Join(", ", order));
        }

        paging.AppendTo(builder, parameters);
        return builder.ToString();
    }

    /// <summary>
    /// The count of the rows the plan selects, counted by the database: one
    /// aggregate row, no columns. It runs on every pushed read because it is
    /// what lets the page say whether more remains without over-fetching a row
    /// past the cap (ADR-0116 §2).
    /// </summary>
    public static string Count(SqlServerDatasetName dataset, string? where)
    {
        var builder = new StringBuilder("SELECT COUNT(*) FROM ").Append(dataset.QuoteQualified());
        if (where is not null)
        {
            builder.Append(" WHERE ").Append(where);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The grouped reduction: the group key, one aggregate expression per
    /// statistic, grouped over the key, ordered by the plan's order, and cut to
    /// the page (ADR-0128, ADR-0133 §4, ADR-0137 §2). The <c>ORDER BY</c> is written before
    /// the page clause and never after it: <c>OFFSET</c>/<c>FETCH NEXT</c> is
    /// legal only over an order, and a reduction that lost the plan's order on
    /// the way to the server would answer the groups in the order the scan
    /// happened to return them (ADR-0128 §8).
    ///
    /// <para>
    /// Returns <c>null</c> — the caller's cue to reduce the rows it read with
    /// the shared reference, which is a cost and never a different answer — when
    /// the dialect cannot state the reference's answer: a grouped reduction
    /// whose plan asked for no order, or asked for one over a value a group row
    /// does not carry (<c>GROUP BY</c> returns rows in no defined order, and
    /// ordering a group by one of its own columns is a different question); a
    /// group key or a statistic whose kind T-SQL groups or reduces the way the
    /// reference does not (a geometry, which groups by its stored bytes here and
    /// by its value in the reference); a statistic T-SQL cannot state at all —
    /// a percentile over a field it cannot rank against another, or an envelope,
    /// because a rectangle is four reduced coordinates and a polygon rather than
    /// one aggregate expression; and a clause over the reduced groups, which
    /// this store does not compile for this dialect.
    /// </para>
    ///
    /// <para>
    /// A percentile is the one statistic T-SQL has no <em>aggregate</em> for —
    /// it is a window function, and a window's ordering column has to be a
    /// grouped one, which the ranked field is not (error 8120). So a reduction
    /// that asks for one is written as a derived table the ranks are taken over,
    /// <c>PARTITION BY</c> the group key, and the grouped statement reduces the
    /// one value each partition ranked: a partition is a group, so the value it
    /// ranked is the value that group reports (ADR-0137 §2). A reduction that
    /// asks for no percentile is the single statement it was.
    /// </para>
    /// </summary>
    public static string? Aggregate(
        SqlServerDatasetName dataset,
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
        ArgumentNullException.ThrowIfNull(parameters);
        // A clause over the reduced groups is declined rather than compiled
        // here: it resolves a name to the statistic's own aggregate expression,
        // which asks this store's predicate compiler a second question it does
        // not yet answer (ADR-0133 §5). The reduction is finished over the rows
        // the restriction selected instead, where the clause is the reference's
        // own.
        if (having is not null)
        {
            return null;
        }

        if (groupColumns.Count == 0 && order.Count > 0)
        {
            // An ungrouped reduction is one group, so a plan's order over it
            // has nothing to order and there is no term this statement could
            // write; a reduction that carries one is finished here.
            return null;
        }

        if (groupColumns.Count > 0
            && (order.Count == 0 || order.Any(term => !groupColumns.Contains(term.Field, StringComparer.Ordinal))))
        {
            return null;
        }

        // A page needs an order to skip over: T-SQL takes `OFFSET`/`FETCH NEXT`
        // only over an `ORDER BY`, and an ungrouped reduction is one group that
        // needs no order — so a page over one is reduced here (ADR-0124 §6).
        var page = paging ?? new Paging(null, 0);
        if (!page.IsWhole && order.Count == 0)
        {
            return null;
        }

        if (groupColumns.Any(column => !Groupable(column, schema)))
        {
            return null;
        }

        var keys = groupColumns.Select(column => Key(column, schema, byteOrderText)).ToArray();
        var statistics = specs.Select(spec => new Statistic(spec, schema, byteOrderText)).ToArray();
        var windowed = statistics.Any(statistic => statistic.IsWindowed);

        // A reduction with no percentile reads the table it reduces; one with a
        // percentile reads a derived table, because a window function cannot be
        // written over the grouped statement it is a statistic of. Every
        // expression that survives the derived table therefore names the derived
        // columns, and a flat one names the table's.
        var qualifier = windowed ? Derived.ToString() : string.Empty;
        var windows = new List<string>();
        var values = new List<string>(specs.Count);
        foreach (var statistic in statistics)
        {
            if (statistic.IsWindowed)
            {
                // The fraction is bound rather than written, so no client text
                // reaches the statement (ADR-0028), and the alias is the
                // window's own position — a result name is a client's text and
                // never becomes an identifier here.
                if (statistic.Window($"w{windows.Count}", keys, parameters) is not { } window)
                {
                    return null;
                }

                windows.Add(window);
                // The grouped statement reduces the one value the partition
                // ranked, and reports it where the request asked for this
                // statistic: the row is read in the request's order, not in the
                // order the statement happens to group the two kinds of
                // statistic into.
                values.Add($"MAX([{qualifier}].[w{windows.Count - 1}])");
                continue;
            }

            if (statistic.Expression(qualifier) is not { } expression)
            {
                return null;
            }

            values.Add(expression);
        }

        var source = windowed
            ? Windowed(dataset, where, groupColumns, keys, statistics, windows)
            : dataset.QuoteQualified();
        // The group key as each statement names it: a windowed statement reads
        // the derived column, which already carries the key expression's
        // code-point collation, and a flat one reads the expression itself.
        var projected = windowed
            ? groupColumns.Select(Quote).ToArray()
            : keys.Select((key, i) => Alias(key, groupColumns[i])).ToArray();
        var grouped = windowed ? projected : keys;
        var builder = new StringBuilder("SELECT ")
            .Append(string.Join(", ", projected.Concat(values)))
            .Append(" FROM ")
            .Append(source);
        if (!windowed && where is not null)
        {
            builder.Append(" WHERE ").Append(where);
        }

        if (groupColumns.Count > 0)
        {
            builder.Append(" GROUP BY ").Append(string.Join(", ", grouped));
            if (order.Count > 0)
            {
                // A grouped statement's `ORDER BY` may only name what the `GROUP BY`
                // groups: a term over the bare column of a text key is a column the
                // grouping does not contain, and T-SQL refuses the statement. So the
                // term is written over the group key expression itself.
                var terms = order.Select(term =>
                    GroupedTerm(grouped[groupColumns.ToList().IndexOf(term.Field)], term.IsDescending));
                builder.Append(" ORDER BY ").Append(string.Join(", ", terms));
            }
        }

        page.AppendTo(builder, parameters);
        return builder.ToString();
    }

    /// <summary>
    /// The derived table a percentile is ranked over: the group key and the
    /// columns the other statistics reduce, then one window column per
    /// percentile, each partitioned by the group key. The partition is written
    /// over the base columns and the <c>GROUP BY</c> over the derived ones,
    /// because they are the same value read on the two sides of the subquery —
    /// and it has to be the key expression with its collation, not the bare
    /// column, or the partition folds <c>"Alpha"</c> onto <c>"alpha"</c> and the
    /// group is not the reference's group (ADR-0121).
    /// </summary>
    private static string Windowed(
        SqlServerDatasetName dataset,
        string? where,
        IReadOnlyList<string> groupColumns,
        IReadOnlyList<string> keys,
        IReadOnlyList<Statistic> statistics,
        IReadOnlyList<string> windows)
    {
        // Every field the grouped statement reduces has to be a column of the
        // derived table, and a field that is both a group key and a reduced one
        // is carried once, under the key's own expression.
        var reduced = statistics
            .Where(statistic => !statistic.IsWindowed && !statistic.IsRowCount)
            .Select(statistic => statistic.Field!)
            .Where(field => !groupColumns.Contains(field, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(field => Alias(Quote(field), field));
        var builder = new StringBuilder("(SELECT ")
            .Append(string.Join(
                ", ",
                keys.Select((key, i) => Alias(key, groupColumns[i])).Concat(reduced).Concat(windows)))
            .Append(" FROM ")
            .Append(dataset.QuoteQualified());
        if (where is not null)
        {
            builder.Append(" WHERE ").Append(where);
        }

        return builder.Append(") AS ").Append(Derived).ToString();
    }

    /// <summary>
    /// The deduplicated field combinations a plan selects, as a
    /// <c>SELECT DISTINCT</c> and the order it is reported in — or <c>null</c>
    /// when this dialect cannot state that order (ADR-0133 §6).
    ///
    /// <para>
    /// The distinct set is pushed for the plan whose order is <em>total over
    /// the distinct rows</em>: every term of the plan's order names a requested
    /// field, and the terms between them cover every requested field. That is
    /// the plan whose first-seen order — which is what the reference reports —
    /// is the order of the rows themselves. Anything else is a set of rows that
    /// tie on the order's terms, and a dialect says nothing about which comes
    /// first, so the set is reduced here over the read, where the first-seen
    /// order is knowable.
    /// </para>
    ///
    /// <para>
    /// The order is written on an outer statement over the <c>DISTINCT</c>,
    /// because both dialects take an <c>ORDER BY</c> over a <c>DISTINCT</c>
    /// only from its own select list, and the reference's null-placement key is
    /// not one of the requested fields. A text field is deduplicated and ordered
    /// under the code-point collation, since a locale collation folds
    /// <c>"A"</c> onto <c>"a"</c> — and the reference counts them as two values
    /// (ADR-0121).
    /// </para>
    /// </summary>
    public static string? Distinct(
        SqlServerDatasetName dataset,
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

        var keys = fields.Select(field => Key(field, schema, byteOrderText)).ToArray();
        var builder = new StringBuilder("SELECT * FROM (SELECT DISTINCT ")
            .Append(string.Join(", ", keys.Select((key, i) => Alias(key, fields[i]))))
            .Append(" FROM ")
            .Append(dataset.QuoteQualified());
        if (where is not null)
        {
            builder.Append(" WHERE ").Append(where);
        }

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
    /// One group order as T-SQL: the null-placement key and the group key
    /// itself, in the direction the plan asked for. The key is the expression
    /// the <c>GROUP BY</c> groups by — a text column under the code-point
    /// collation — because a term over anything else is not a column this
    /// grouped statement contains.
    /// </summary>
    private static string GroupedTerm(string key, bool descending)
    {
        var nullFirst = descending;
        return $"CASE WHEN {key} IS NULL THEN {(nullFirst ? 0 : 1)} ELSE {(nullFirst ? 1 : 0)} END"
            + $", {key} {(descending ? "DESC" : "ASC")}";
    }

    /// <summary>
    /// Whether a <c>GROUP BY</c> over this column groups the rows the way the
    /// reference does. Every kind but a geometry groups by its value, and a
    /// geometry groups by its stored bytes here and by its value there, so a
    /// geometry key is not this statement's.
    /// </summary>
    private static bool Groupable(string column, IFeatureSchema schema)
    {
        var index = schema.IndexOf(column);
        return index >= 0 && schema[index].Kind != AttributeKind.Geometry;
    }

    /// <summary>
    /// One group key as an expression that groups the way the reference groups
    /// and reads as the value it names: a text column under the code-point
    /// collation, everything else as itself.
    /// </summary>
    private static string Key(string column, IFeatureSchema schema, bool byteOrderText) =>
        Ordered(column, schema, byteOrderText);

    /// <summary>
    /// An expression named for the column it came from, so the derived row can
    /// be read by the plan's own field names — for the expression that is not
    /// already spelled as the column.
    /// </summary>
    private static string Alias(string expression, string column) =>
        expression == Quote(column) ? expression : $"{expression} AS {Quote(column)}";

    /// <summary>
    /// One aggregate expression in the dialect's own spelling, or <c>null</c>
    /// when T-SQL has no aggregate that answers this statistic (ADR-0133 §3).
    /// <paramref name="qualifier"/> names the table the expression reads
    /// <em>from</em>, which is the derived table when a percentile is in the
    /// request and the dataset's own table when none is.
    /// </summary>
    private sealed class Statistic(AggregateSpec spec, IFeatureSchema schema, bool byteOrderText)
    {
        /// <summary>The field this statistic reduces, for a fielded statistic.</summary>
        public string? Field => spec.IsRowCount ? null : spec.Field;

        /// <summary>Whether this statistic reduces every row rather than one field's values.</summary>
        public bool IsRowCount => spec.IsRowCount;

        /// <summary>
        /// Whether this statistic is a window function rather than an aggregate:
        /// T-SQL's percentile is the one statistic with no aggregate spelling
        /// (ADR-0137 §2).
        /// </summary>
        public bool IsWindowed => spec.Statistic
            is AggregateStatistic.PercentileContinuous or AggregateStatistic.PercentileDiscrete;

        public string? Expression(string qualifier)
        {
            if (IsRowCount)
            {
                return "COUNT(*)";
            }

            var index = schema.IndexOf(spec.Field);
            if (index < 0)
            {
                return null;
            }

            var kind = schema[index].Kind;
            var quoted = Qualified(spec.Field, qualifier);
            return spec.Statistic switch
            {
                // `COUNT` and `SUM` skip the nulls, and report a null for a
                // group with none of them, exactly as the reference does.
                AggregateStatistic.Count => $"COUNT({quoted})",
                // `SUM` over an integer column is a `bigint` sum here: the
                // column may be a hand-authored `int`, whose `SUM` overflows in
                // the server where the reference's `long` sum does not.
                AggregateStatistic.Sum => kind == AttributeKind.Int64
                    ? $"SUM(CONVERT(bigint, {quoted}))"
                    : kind == AttributeKind.Double ? $"SUM({quoted})" : null,
                // `AVG` over an integer column is *integer* division — a
                // truncated mean, not the reference's — and `float` is T-SQL's
                // double, so the sum is taken in it and divided once at the end,
                // the way the reference averages.
                AggregateStatistic.Average => kind == AttributeKind.Int64
                    ? $"SUM(CONVERT(float, {quoted})) / COUNT({quoted})"
                    : kind == AttributeKind.Double ? $"SUM({quoted}) / COUNT({quoted})" : null,
                // `VAR` and `STDEV` are the *sample* forms, which is the form
                // the reference reports, and are null for fewer than two values.
                AggregateStatistic.Variance => Numeric(kind, quoted) is { } numeric ? $"VAR({numeric})" : null,
                AggregateStatistic.StdDev => Numeric(kind, quoted) is { } numeric ? $"STDEV({numeric})" : null,
                // An extreme is the smallest/largest value under the same
                // comparison every other string here states. A `bit` has no
                // `MIN`/`MAX` in T-SQL at all, but it is two integers and the
                // contract's order over a boolean is false before true — so the
                // extreme is taken over `0`/`1` and read back as the boolean the
                // reference reports it as (ADR-0137 §4). A geometry has neither a
                // comparison T-SQL and .NET share nor a sort key this contract
                // could state.
                AggregateStatistic.Minimum => Extreme(kind, spec.Field, schema, byteOrderText, "MIN", qualifier),
                AggregateStatistic.Maximum => Extreme(kind, spec.Field, schema, byteOrderText, "MAX", qualifier),
                // The envelope is four reduced coordinates and a polygon rather
                // than an expression, and the percentiles are written as windows
                // over the derived table instead.
                _ => null,
            };
        }

        /// <summary>
        /// A percentile as T-SQL spells it — a window function over a partition,
        /// which is a group — ranked ascending or descending as the statistic
        /// asked, with the fraction bound rather than written (ADR-0028) and the
        /// partition written over the group key (ADR-0137 §2).
        ///
        /// <para>
        /// It is a cost and never a different answer for the two cases it is
        /// refused: a field the dataset does not have, and a field this dialect
        /// cannot rank against another — T-SQL refuses a percentile whose ordering
        /// column and its value are of different types (error 402), which every
        /// text, boolean, geometry, date and GUID field is.
        /// </para>
        /// </summary>
        public string? Window(string alias, string[] partition, List<object?> parameters)
        {
            var index = schema.IndexOf(spec.Field);
            if (index < 0 || Numeric(schema[index].Kind, Quote(spec.Field)) is null)
            {
                return null;
            }

            parameters.Add(spec.PercentileFraction);
            var name = spec.Statistic == AggregateStatistic.PercentileContinuous ? "PERCENTILE_CONT" : "PERCENTILE_DISC";
            var fraction = $"@p{parameters.Count - 1}";
            var direction = spec.PercentileDescending ? "DESC" : "ASC";
            var window = partition.Length == 0 ? "OVER ()" : $"OVER (PARTITION BY {string.Join(", ", partition)})";
            return $"{name}({fraction}) WITHIN GROUP (ORDER BY {Quote(spec.Field)} {direction}) {window} AS {Quote(alias)}";
        }

        private static string? Numeric(AttributeKind kind, string quoted) => kind switch
        {
            AttributeKind.Int64 => $"CONVERT(float, {quoted})",
            AttributeKind.Double => quoted,
            _ => null,
        };

        /// <summary>
        /// An extreme is the smallest/largest value under the same
        /// comparison every other string here states. A <c>bit</c> has no
        /// <c>MIN</c>/<c>MAX</c> in T-SQL at all, and a geometry has neither a
        /// comparison T-SQL and .NET share nor a sort key this contract could
        /// state.
        /// </summary>
        private static string? Extreme(
            AttributeKind kind,
            string column,
            IFeatureSchema schema,
            bool byteOrderText,
            string function,
            string qualifier)
        {
            var quoted = Qualified(column, qualifier);
            return kind switch
            {
                AttributeKind.Boolean => $"{function}(CONVERT(int, {quoted}))",
                AttributeKind.Geometry => null,
                _ => $"{function}({Ordered(column, schema, byteOrderText, qualifier)})",
            };
        }
    }

    /// <summary>
    /// The projection of a plan: the requested fields in the requested order,
    /// or every field when the plan projects nothing. A geometry field is read
    /// as canonical WKB, exactly as a scan reads it.
    /// </summary>
    public static IReadOnlyList<string> Columns(IFeatureSchema schema, IReadOnlyList<string>? projection)
    {
        var fields = projection is null or { Count: 0 }
            ? schema.Fields
            : projection
                .Where(field => field != AggregateSpec.AllFields)
                .Select(field => schema[schema.IndexOf(field)])
                .ToArray();
        return fields.Select(Column).ToArray();
    }

    /// <summary>
    /// The order of a plan as T-SQL, or <c>null</c> when the plan asked for no
    /// order, or the table cannot make the requested order total in the
    /// reference's terms.
    ///
    /// <para>
    /// A plan that does ask for one gets each term with its explicit null
    /// placement, each text term under the code-point collation, and then the
    /// dataset's identity columns as the contract's mandatory tie-break. Three
    /// plans have no order here: a plan that asked for none, a table with no
    /// identity column to tie-break with, and an identity this dialect cannot
    /// render the way the contract's tie-break is written (see
    /// <see cref="TieBreak"/>). A page over an order that is not total is not a
    /// position the next statement can name, so each of them is finished with
    /// the reference executor over the rows the store selected instead.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string>? Order(
        IReadOnlyList<OrderTerm> order, IReadOnlyList<string> identityColumns, IFeatureSchema schema, bool byteOrderText)
    {
        if (order.Count == 0 || identityColumns.Count == 0)
        {
            return null;
        }

        var terms = Terms(order, schema, byteOrderText);
        if (terms is null || TieBreak(identityColumns, schema) is not { } tie)
        {
            return null;
        }

        return [.. terms, $"{tie} ASC"];
    }

    /// <summary>
    /// The requested sort keys as T-SQL, or <c>null</c> when one of them names a
    /// field the dataset does not have — a plan the schema does not admit is the
    /// typed <c>invalid.arguments</c> the contract promises, not an
    /// <c>ORDER BY</c> over a column that is not there.
    /// </summary>
    public static IReadOnlyList<string>? Terms(
        IReadOnlyList<OrderTerm> order, IFeatureSchema schema, bool byteOrderText)
    {
        var terms = new List<string>();
        foreach (var term in order)
        {
            if (schema.IndexOf(term.Field) < 0)
            {
                return null;
            }

            terms.Add(Term(term, schema, byteOrderText));
        }

        return terms;
    }

    /// <summary>
    /// The contract's mandatory tie-break — the feature identity as one string,
    /// which is what the reference orders by when two rows share a requested
    /// key — as a T-SQL expression over the identity columns, or
    /// <c>null</c> when this dialect cannot render it the same way .NET does.
    ///
    /// <para>
    /// It has to be the <em>id string</em>, not the identity columns' own
    /// values, because that is the key the reference breaks ties with: two rows
    /// whose sort key ties are ordered <c>"10"</c> before <c>"9"</c> there and
    /// 9 before 10 over the column, and a page cut between them is a page the
    /// reference never handed out. So each column is rendered the way
    /// <see cref="SqlServerDiagnostics"/> renders a feature id — the
    /// joined <c>ToString()</c> of the value — and a column whose T-SQL
    /// rendering is not that rendering disqualifies the pushdown rather than
    /// answering a different question: a <c>float</c> converts with six
    /// significant digits where <c>double.ToString()</c> keeps every one, a
    /// <c>bit</c> is <c>1</c>/<c>0</c> where .NET writes <c>True</c>, and a
    /// <c>datetimeoffset</c> formats by culture in one and by the server's
    /// settings in the other. A <c>uniqueidentifier</c> needs the lower-casing
    /// below, since T-SQL writes it upper-case and <see cref="Guid.ToString()"/>
    /// does not.
    /// </para>
    ///
    /// <para>
    /// The collation is unconditional here, whatever the database's own: the
    /// reference compares ids ordinally, so a tie-break under a locale
    /// collation would be a different order for the same reason a pushed text
    /// key is (ADR-0121). A primary key column is never null, so the
    /// tie-break needs no null-placement key.
    /// </para>
    /// </summary>
    public static string? TieBreak(IReadOnlyList<string> identityColumns, IFeatureSchema schema)
    {
        var parts = new List<string>(identityColumns.Count);
        foreach (var column in identityColumns)
        {
            var index = schema.IndexOf(column);
            if (index < 0)
            {
                return null;
            }

            if (Rendered(column, schema[index].Kind) is not { } rendered)
            {
                return null;
            }

            parts.Add(rendered);
        }

        // CONCAT joins with '|' and renders a null as the empty string, which
        // is exactly what the feature id is: `string.Join('|', …)` over
        // `value?.ToString() ?? ""`.
        var joined = parts.Count == 1 ? parts[0] : $"CONCAT({string.Join(", ", parts)})";
        return $"{joined} COLLATE {SqlServerTextCollation.ByteOrder}";
    }

    /// <summary>
    /// One identity column as the text the contract's id string carries for it,
    /// or <c>null</c> when T-SQL renders the column's value as some other text.
    /// A text column is read through <c>CONVERT</c> for the reason every text
    /// term is: a <c>text</c>/<c>ntext</c> column cannot be sorted at all.
    /// </summary>
    private static string? Rendered(string column, AttributeKind kind) =>
        kind switch
        {
            AttributeKind.Int64 => $"CONVERT(nvarchar(max), {Quote(column)})",
            AttributeKind.String => $"CONVERT(nvarchar(max), {Quote(column)})",
            AttributeKind.Guid => $"LOWER(CONVERT(nvarchar(36), {Quote(column)}))",
            _ => null,
        };

    /// <summary>
    /// One requested sort key as T-SQL: the null-placement key, then the
    /// ordered column. A descending key places the nulls first, so its
    /// <c>CASE</c> is the mirror of the ascending one — T-SQL's own null
    /// placement is the opposite in both directions.
    /// </summary>
    private static string Term(OrderTerm term, IFeatureSchema schema, bool byteOrderText, string qualifier = "")
    {
        var quoted = Qualified(term.Field, qualifier);
        var direction = term.IsDescending ? "DESC" : "ASC";
        var nullFirst = term.IsDescending;
        return $"CASE WHEN {quoted} IS NULL THEN {(nullFirst ? 0 : 1)} ELSE {(nullFirst ? 1 : 0)} END"
            + $", {Ordered(term.Field, schema, byteOrderText, qualifier)} {direction}";
    }

    /// <summary>The column as this statement spells it, qualified when the sort runs over a derived row.</summary>
    private static string Qualified(string column, string qualifier) =>
        qualifier.Length == 0 ? Quote(column) : $"{Quote(qualifier)}.{Quote(column)}";

    /// <summary>
    /// A column as an expression the contract's comparison can be written
    /// against: the bracketed column, and — for a <em>text</em> column — a
    /// conversion to <c>nvarchar(max)</c> (a <c>text</c>/<c>ntext</c> column
    /// cannot be sorted at all) under the code-point collation unless the
    /// database already compares that way.
    ///
    /// <para>
    /// Every string comparison this file writes goes through here, because they
    /// are one question: an <c>ORDER BY</c> over a text sort key and the
    /// identity tie-break a page boundary is cut on all inherit the column's
    /// collation, and the contract's answer is an ordinal one. A numeric,
    /// date-time or boolean column takes neither term: <c>COLLATE</c> is a
    /// string operator, and <c>ORDER BY population COLLATE …</c> is a
    /// statement T-SQL refuses.
    /// </para>
    /// </summary>
    private static string Ordered(string column, IFeatureSchema schema, bool byteOrderText, string qualifier = "")
    {
        var index = schema.IndexOf(column);
        if (index < 0 || schema[index].Kind != AttributeKind.String)
        {
            return Qualified(column, qualifier);
        }

        var converted = $"CONVERT(nvarchar(max), {Qualified(column, qualifier)})";
        return byteOrderText ? converted : $"{converted} COLLATE {SqlServerTextCollation.ByteOrder}";
    }

    /// <summary>One field as a select column: geometry is read as canonical WKB, everything else by name.</summary>
    private static string Column(FieldDefinition field) =>
        field.Kind == AttributeKind.Geometry ? $"{Quote(field.Name)}.STAsBinary()" : Quote(field.Name);

    private static string Quote(string column) => SqlServerIdentifier.Quote(column);

    /// <summary>The row cap and the page start, both bound values.</summary>
    internal readonly record struct Paging(int? Limit, int Offset)
    {
        /// <summary>Whether this page is the whole plan, so neither clause is written.</summary>
        public bool IsWhole => Limit is null && Offset == 0;

        /// <summary>
        /// The page clause. <c>FETCH NEXT</c> follows <c>OFFSET</c> and is
        /// written only with a cap, because a start on its own is a read of the
        /// rest of the order.
        /// </summary>
        public void AppendTo(StringBuilder builder, List<object?> parameters)
        {
            if (IsWhole)
            {
                return;
            }

            builder.Append(" OFFSET ").Append(Parameter(parameters, Offset)).Append(" ROWS");
            if (Limit is { } limit)
            {
                builder.Append(" FETCH NEXT ").Append(Parameter(parameters, limit)).Append(" ROWS ONLY");
            }
        }

        private static string Parameter(List<object?> parameters, object? value)
        {
            parameters.Add(value);
            return $"@p{parameters.Count - 1}";
        }
    }
}
