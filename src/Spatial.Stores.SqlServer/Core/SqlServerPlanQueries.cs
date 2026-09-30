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
/// </summary>
internal static class SqlServerPlanQueries
{
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
    private static string Term(OrderTerm term, IFeatureSchema schema, bool byteOrderText)
    {
        var quoted = Quote(term.Field);
        var direction = term.IsDescending ? "DESC" : "ASC";
        var nullFirst = term.IsDescending;
        return $"CASE WHEN {quoted} IS NULL THEN {(nullFirst ? 0 : 1)} ELSE {(nullFirst ? 1 : 0)} END"
            + $", {Ordered(term.Field, schema, byteOrderText)} {direction}";
    }

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
    private static string Ordered(string column, IFeatureSchema schema, bool byteOrderText)
    {
        var index = schema.IndexOf(column);
        if (index < 0 || schema[index].Kind != AttributeKind.String)
        {
            return Quote(column);
        }

        var converted = $"CONVERT(nvarchar(max), {Quote(column)})";
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
