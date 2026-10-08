using System.Globalization;
using System.Text;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.Stores.PostGIS.Core;

/// <summary>
/// Compiles a <see cref="Predicate"/> into one fully parameterised
/// <c>WHERE</c> fragment (ADR-0074 §3-4, ADR-0028: parameterised attribute
/// filtering). Fields resolve against the dataset's discovered
/// <see cref="FeatureSchema"/> — an unknown name is a typed invalid-argument
/// failure naming the available fields, and a geometry field is rejected with
/// a hint to filter spatially with the bounding box instead. Every literal
/// becomes one positional parameter (<c>@p0</c>, <c>@p1</c>, …) whose value is
/// appended to <c>parameters</c> in order, so the SQL never contains a
/// client-supplied value. The grammar itself is parsed once at the boundary
/// (<c>Spatial.Core.Features.Query.FeatureFilterText</c>); this is the thin
/// PostGIS half of the back end, and the identifier quoting, the
/// <c>!=</c> spelling and the envelope test are the whole of its dialect.
/// Built fragments are deterministic for equal plans.
///
/// <para>
/// Every string comparison it writes says which order it wants: the column is
/// rendered by <see cref="PostgisPlanQueries.Ordered"/>, the same helper the
/// <c>ORDER BY</c> terms go through, so a <c>WHERE</c> cannot inherit the
/// database's collation where the order already refuses to (ADR-0098 §3,
/// ADR-0121, ADR-0123). <see cref="ComparesText"/> is what a caller asks
/// before reading that collation, so a plan that compares no text by bytes never
/// pays for the catalog read.
/// </para>
///
/// <para>
/// The one exception is the folded pattern (ADR-0132), which states its own
/// case behaviour with <see cref="Fold"/> rather than inheriting one: Postgres's
/// <c>ILIKE</c> folds under the database's collation, so it is
/// <c>Épsilon</c> for <c>epsilon</c> on a stock container and byte-exact on a
/// <c>C</c> one, and neither is the reference evaluator's answer. A folded
/// comparison therefore takes no <c>COLLATE</c> term — it takes the fold, which
/// is the same on every database.
/// </para>
/// </summary>
internal static class PostgisPredicateSql
{
    /// <summary>The alphabet a folded pattern folds: <c>A</c>–<c>Z</c>.</summary>
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>The alphabet a folded pattern folds to: <c>a</c>–<c>z</c>.</summary>
    private const string Lower = "abcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// The expression that folds an operand over the ASCII alphabet (ADR-0132),
    /// which is the fold the reference evaluator applies to a value and a
    /// pattern before the same whole-value test runs. It is written out rather
    /// than reached for through <c>lower()</c> or <c>ILIKE</c> because both of
    /// those are the database's locale: <c>lower()</c> under a Turkish collation
    /// folds <c>I</c> to a dotless <c>ı</c>, and <c>ILIKE</c> under <c>C</c>
    /// folds nothing at all, so a plan would answer differently on two
    /// deployments of the same store. The two alphabets are engine text, not
    /// client text, and every literal still binds as a parameter (ADR-0028).
    /// </summary>
    public static string Fold(string expression) => $"translate({expression}, '{Upper}', '{Lower}')";

    /// <summary>
    /// The literal a folded pattern binds as: the pattern with the same ASCII
    /// fold the column expression applies, which is the value the reference
    /// evaluator compares against. It is the re-encoding of a literal to its
    /// column's kind that this compiler already does for a guid and an instant,
    /// for the same reason — the server has no operator for the unfolded pair
    /// that means the same thing.
    /// </summary>
    internal static string FoldedPattern(string pattern)
    {
        var builder = new StringBuilder(pattern.Length);
        foreach (var character in pattern)
        {
            builder.Append(character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds the <c>WHERE</c> fragment (without the keyword) for a plan's
    /// predicate: the bounding-box pre-filter and the attribute predicate
    /// combined, or null when the plan selects everything.
    /// </summary>
    public static string? Build(
        DatasetDescription description,
        BoundingBox? bbox,
        Predicate? where,
        PostgisTextOrder text,
        List<object?> parameters)
    {
        var prefilter = BoundingBox(bbox, description, parameters);
        var filter = where is null ? null : Where(where, description.Schema, text, parameters);
        return Combine(prefilter, filter);
    }

    /// <summary>Builds the attribute fragment, or fails with an actionable error for an unknown or geometry field.</summary>
    public static string Where(Predicate where, IFeatureSchema schema, PostgisTextOrder text, List<object?> parameters)
    {
        var builder = new SqlBuilder(schema, text, parameters);
        builder.Visit(where);
        return builder.Error is { } error ? throw SpatialException.BadArguments(error) : builder.ToString();
    }

    /// <summary>
    /// One operand of a clause over a reduced group row: the SQL it is compared
    /// as, and the kind its value compares under (ADR-0128).
    /// </summary>
    /// <param name="Sql">The operand's SQL — a column for a group key, a statistic's aggregate expression for a result.</param>
    /// <param name="Kind">The kind the value compares under, which is what binds the literal.</param>
    internal readonly record struct GroupColumn(string Sql, AttributeKind Kind);

    /// <summary>
    /// Builds the <c>HAVING</c> fragment (without the keyword) for a reduction's
    /// clause over its groups, or <c>null</c> when the clause names a value the
    /// group row does not carry.
    ///
    /// <para>
    /// It is the same builder as the <c>WHERE</c> fragment over a different
    /// resolution: instead of a schema's columns, the caller's
    /// <paramref name="resolve"/> answers with the operand to compare. So the
    /// tree walk, the operator spellings, the literal binding and the
    /// three-valued reading of a null are one definition, and a clause a store
    /// pushes into a <c>HAVING</c> answers what the reference evaluator answers
    /// for the reduced value.
    /// </para>
    /// </summary>
    public static string? Having(
        Predicate having,
        Func<FieldRef, GroupColumn?> resolve,
        List<object?> parameters)
    {
        var builder = new SqlBuilder(resolve, parameters);
        builder.Visit(having);
        return builder.Error is not null ? null : builder.ToString();
    }

    /// <summary>
    /// Whether a predicate compares a text column at all, which is the only
    /// part of a <c>WHERE</c> the database's collation can change. A caller
    /// reads the collation when this is true and skips the catalog read
    /// otherwise: a plan over a bounding box and a number is not a question the
    /// collation answers, and a store that asked anyway would pay a round trip
    /// for a plan that cannot use the answer.
    /// </summary>
    public static bool ComparesText(Predicate where, IFeatureSchema schema) => Text(where, schema);

    private static bool Text(Predicate predicate, IFeatureSchema schema) => predicate switch
    {
        // A folded pattern states its own fold, so it is not a comparison the
        // database's collation can change and a plan carrying only one cannot
        // use the byte-order answer. A byte-ordered comparison beside it still
        // can, so the walk asks about the rest of the tree.
        Predicate.Compare compare => IsText(compare.Field, schema) && !PredicateCompatibility.FoldsCase(compare.Operator),
        Predicate.IsIn isIn => IsText(isIn.Field, schema),
        Predicate.Every every => every.Terms.Any(term => Text(term, schema)),
        Predicate.Some some => some.Terms.Any(term => Text(term, schema)),
        _ => false,
    };

    private static bool IsText(FieldRef field, IFeatureSchema schema)
    {
        var index = schema.IndexOf(field.Name);
        return index >= 0 && schema[index].Kind == AttributeKind.String;
    }

    /// <summary>Builds the bounded-box spatial predicate and appends its bound values.</summary>
    public static string? BoundingBox(
        Spatial.Contracts.BoundingBox? bbox, DatasetDescription description, List<object?> parameters)
    {
        if (bbox is null)
        {
            return null;
        }

        var builder = new SqlBuilder(null!, PostgisTextOrder.ByteOrder, parameters);
        return builder.AppendBoundingBox(
            description.GeometryColumn,
            description.Srid,
            bbox.MinX,
            bbox.MinY,
            bbox.MaxX,
            bbox.MaxY);
    }

    private static string? Combine(string? prefilter, string? filter) => (prefilter, filter) switch
    {
        (null, null) => null,
        (null, _) => filter,
        (_, null) => prefilter,
        _ => $"({prefilter}) AND ({filter})",
    };

    private sealed class SqlBuilder
    {
        private readonly IFeatureSchema? _schema;
        private readonly Func<FieldRef, GroupColumn?>? _resolve;
        private readonly PostgisTextOrder _text;
        private readonly List<object?> _parameters;
        private readonly StringBuilder _sql = new();
        private int _parameterIndex;

        public SqlBuilder(IFeatureSchema? schema, PostgisTextOrder text, List<object?> parameters)
        {
            _schema = schema;
            _text = text;
            _parameters = parameters;
            // Continue placeholder numbering from already-bound values so a
            // bbox predicate combined with an attribute filter never reuses
            // @p0..@pn (each literal becomes one positional parameter in order).
            _parameterIndex = parameters.Count;
        }

        /// <summary>A builder over a group row rather than a dataset's columns (a <c>HAVING</c> clause).</summary>
        public SqlBuilder(Func<FieldRef, GroupColumn?> resolve, List<object?> parameters)
            : this(schema: null, text: PostgisTextOrder.Locale, parameters)
        {
            _resolve = resolve;
        }

        public string? Error { get; private set; }

        /// <summary>The group-row operand the last resolved name stood for, when this builder is a <c>HAVING</c> one.</summary>
        private GroupColumn? _group;

        public string AppendBoundingBox(
            string geometryColumn,
            int srid,
            double minx,
            double miny,
            double maxx,
            double maxy)
        {
            _sql.Append('"')
                .Append(geometryColumn)
                .Append("\" && ST_MakeEnvelope(")
                .Append(Parameter(minx)).Append(", ")
                .Append(Parameter(miny)).Append(", ")
                .Append(Parameter(maxx)).Append(", ")
                .Append(Parameter(maxy))
                .Append(", ").Append(srid).Append(')');
            return _sql.ToString();
        }

        public void Visit(Predicate predicate)
        {
            switch (predicate)
            {
                case Predicate.Every every:
                    VisitTerms(every.Terms, "AND");
                    break;
                case Predicate.Some some:
                    VisitTerms(some.Terms, "OR");
                    break;
                case Predicate.Compare compare:
                    VisitCompare(compare);
                    break;
                case Predicate.IsNull isNull:
                    VisitIsNull(isNull);
                    break;
                case Predicate.IsIn isIn:
                    VisitIsIn(isIn);
                    break;
                case Predicate.Constant constant:
                    _sql.Append(constant.Value ? "TRUE" : "FALSE");
                    break;
            }
        }

        public override string ToString() => _sql.ToString();

        private void VisitTerms(IReadOnlyList<Predicate> terms, string conjunction)
        {
            for (var i = 0; i < terms.Count; i++)
            {
                if (i > 0)
                {
                    _sql.Append(' ').Append(conjunction).Append(' ');
                }

                var term = terms[i];
                var needsParens = NeedsGrouping(term, conjunction);
                if (needsParens)
                {
                    _sql.Append('(');
                }

                Visit(term);
                if (needsParens)
                {
                    _sql.Append(')');
                }
            }
        }

        /// <summary>A nested group of a different operator must be parenthesised to preserve precedence.</summary>
        private static bool NeedsGrouping(Predicate term, string conjunction) => term switch
        {
            Predicate.Every group when conjunction == "OR" => group.Terms.Count > 1,
            Predicate.Some group when conjunction == "AND" => group.Terms.Count > 1,
            _ => false,
        };

        private void VisitCompare(Predicate.Compare compare)
        {
            if (!TryResolveColumn(compare.Field, out var index, out var kind))
            {
                return;
            }

            if (kind == AttributeKind.Geometry)
            {
                Error = GeometryHint(compare.Field);
                return;
            }

            if (compare.Value.Kind == LiteralKind.Null)
            {
                // Comparing with NULL is never true in SQL, whatever the
                // operator — the same answer the memory evaluator gives.
                _sql.Append(compare.Operator == ComparisonOperator.NotEquals ? "TRUE" : "FALSE");
                return;
            }

            // A LIKE is a whole-value text test, and a literal of a kind the
            // column cannot be compared with is a comparison that matches
            // nothing. Both are the reference evaluator's answer, and neither
            // is a dialect this server could answer at all.
            if (!TryBind(compare.Operator, compare.Value, kind, out var comparable))
            {
                _sql.Append("FALSE");
                return;
            }

            // The folded pattern states its own case behaviour, so its operand is
            // the column under the ASCII fold and takes no `COLLATE` term: the
            // fold is the same on every database, and a term would be a second,
            // conflicting statement of the same comparison's case.
            if (PredicateCompatibility.FoldsCase(compare.Operator))
            {
                _sql.Append(Fold(FoldedOperand(index)))
                    .Append(" LIKE ")
                    .Append(Parameter(FoldedPattern((string)comparable!)));
                return;
            }

            _sql.Append(Operand(index))
                .Append(' ')
                .Append(SqlOperator(compare.Operator))
                .Append(' ')
                .Append(Parameter(comparable));
        }

        private void VisitIsNull(Predicate.IsNull isNull)
        {
            if (!TryResolveColumn(isNull.Field, out var index, out _))
            {
                return;
            }

            // A null test on a filtered column is the column alone: `COLLATE` is
            // a string operator on a comparison, and a collation never changes
            // whether a value is null. A group row's operand is its own SQL
            // either way, which for a statistic is the aggregate that answers
            // null for a group with nothing to reduce.
            _sql.Append(_resolve is not null ? _group!.Value.Sql : Quote(_schema![index].Name)).Append(" IS ")
                .Append(isNull.Negated ? "NOT NULL" : "NULL");
        }

        private void VisitIsIn(Predicate.IsIn isIn)
        {
            if (!TryResolveIsInColumn(isIn, out var index, out var kind))
            {
                return;
            }

            if (ContainsNull(isIn))
            {
                // `x IN (1, NULL)` and `x NOT IN (1, NULL)` are both unanswerable
                // in SQL, and the grammar refuses a NULL inside IN, so this is
                // only a hand-built plan: answer it the way SQL's three-valued
                // logic reads as "not matched".
                _sql.Append("FALSE");
                return;
            }

            var bindable = BindInValues(isIn, kind);
            if (bindable.Length == 0)
            {
                // A value the column can never equal is not an error and does not
                // poison the list — it simply is not a match, so it drops out.
                // When nothing is left the test answers the negated or plain truth value.
                _sql.Append(isIn.Negated ? "TRUE" : "FALSE");
                return;
            }

            AppendInList(index, isIn.Negated, bindable);
        }

        /// <summary>
        /// The filtered column an <c>IN</c> test resolves against: the same
        /// resolution every other clause gets, plus the geometry refusal —
        /// a geometry field is filtered spatially, never by value.
        /// </summary>
        private bool TryResolveIsInColumn(Predicate.IsIn isIn, out int index, out AttributeKind kind)
        {
            if (!TryResolveColumn(isIn.Field, out index, out kind))
            {
                return false;
            }

            if (kind == AttributeKind.Geometry)
            {
                Error = GeometryHint(isIn.Field);
                return false;
            }

            return true;
        }

        /// <summary>Whether the value list carries a null, which SQL's three-valued logic reads as "not matched".</summary>
        private static bool ContainsNull(Predicate.IsIn isIn) =>
            isIn.Values.Any(value => value.Kind == LiteralKind.Null);

        /// <summary>
        /// The list values bound as the column's kind, with the values the
        /// column can never equal dropped: a comparable literal never binds to
        /// null, so null is the marker for "this value is not a match and
        /// drops out".
        /// </summary>
        private static object?[] BindInValues(Predicate.IsIn isIn, AttributeKind kind) =>
            isIn.Values
                .Select(value => TryBind(ComparisonOperator.Equals, value, kind, out var bound) ? bound : null)
                .Where(bound => bound is not null)
                .ToArray();

        /// <summary>The membership test itself over the values that survived binding.</summary>
        private void AppendInList(int index, bool negated, object?[] bindable)
        {
            _sql.Append(Operand(index))
                .Append(negated ? " NOT IN (" : " IN (");
            for (var i = 0; i < bindable.Length; i++)
            {
                if (i > 0)
                {
                    _sql.Append(", ");
                }

                _sql.Append(Parameter(bindable[i]));
            }

            _sql.Append(')');
        }

        private static string GeometryHint(FieldRef field) =>
            $"the filter column '{field.Name}' is a geometry field; filter spatially with the bounding box (minx/miny/maxx/maxy) instead.";

        /// <summary>
        /// The value a literal binds as when it is compared with a column of
        /// kind <paramref name="kind"/>, or false when the two cannot be
        /// compared at all — in which case the comparison matches nothing, the
        /// answer the reference evaluator gives.
        /// <para>
        /// The column's kind is what the driver must bind, not the literal's
        /// own: the server has no operator for a pair the reference evaluator
        /// would not coerce (ADR-0097 §2), so binding the literal's own kind
        /// asks it about types it cannot answer, which fails at execution
        /// rather than matching no rows. Which pairs are answerable at all is
        /// <see cref="PredicateCompatibility"/>'s one table; only the binding
        /// is this dialect's.
        /// </para>
        /// </summary>
        private static bool TryBind(ComparisonOperator comparison, Literal literal, AttributeKind kind, out object? value)
        {
            value = null;
            if (!PredicateCompatibility.CanMatch(comparison, literal, kind))
            {
                return false;
            }

            value = kind switch
            {
                // A guid and an instant both have a column type of their own, and
                // a number is the same point on the same epoch axis as a
                // date-time literal, so both are re-encoded losslessly into it
                // rather than bound as the number the client wrote. The server
                // has no `uuid = text` and no `timestamptz = bigint` to compare
                // otherwise, and an answer of "no rows" instead would be a
                // different answer from the reference evaluator's.
                AttributeKind.Guid => Guid.Parse(literal.Text ?? string.Empty),
                AttributeKind.DateTimeOffset => Instant(literal),
                _ => Bind(literal),
            };
            return true;
        }

        /// <summary>
        /// The instant a literal denotes, in epoch milliseconds: a date-time
        /// literal carries it, and a number is already the same axis. The
        /// fraction is kept, so a sub-millisecond bound compares the way the
        /// reference evaluator compares it.
        /// </summary>
        private static DateTimeOffset Instant(Literal literal)
        {
            if (literal.Kind == LiteralKind.DateTime)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds((long)literal.Number);
            }

            var milliseconds = literal.Kind == LiteralKind.Integer
                && long.TryParse(literal.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)
                ? whole
                : (long)literal.Number;

            // The fraction of a fractional literal is kept, because the
            // reference evaluator compares the right-hand side as the double the
            // client wrote: truncating it here would move the bound and answer a
            // question the plan did not ask.
            var fraction = literal.Number - Math.Truncate(literal.Number);
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                .AddTicks((long)(fraction * TimeSpan.TicksPerMillisecond));
        }

        /// <summary>
        /// The bound value of a comparable literal: a string, a whole number
        /// carried verbatim, a double, a bool or a UTC instant for a
        /// date-time. The parameter's CLR type is what the driver binds, so
        /// nothing is re-parsed on the server.
        /// </summary>
        private static object Bind(Literal value) => value.Kind switch
        {
            LiteralKind.String => value.Text ?? string.Empty,
            LiteralKind.Integer => BindInteger(value),
            LiteralKind.Decimal => value.Number,
            LiteralKind.Boolean => value.Boolean,
            _ => BindExtended(value),
        };

        /// <summary>
        /// The rarely-bound arms: the UTC instant for a date-time,
        /// <see cref="DBNull"/> for an explicit NULL, and a typed refusal for
        /// a kind no column binds. The compatibility table admits only the
        /// arms above to a comparison, so the last two are the refusal the
        /// compiler keeps rather than a second classification of the vocabulary.
        /// </summary>
        private static object BindExtended(Literal value) => value.Kind switch
        {
            LiteralKind.DateTime => BindDateTime(value),
            LiteralKind.Null => DBNull.Value,
            _ => throw SpatialException.BadArguments($"the filter literal '{value.Text}' is not a bindable value."),
        };

        /// <summary>
        /// A whole number carried verbatim: the literal keeps its text so no
        /// precision is lost, and binds as a long when it parses as one.
        /// </summary>
        private static object BindInteger(Literal value)
        {
            if (long.TryParse(value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            {
                return integer;
            }

            return value.Number;
        }

        /// <summary>A date-time literal as the UTC instant the driver binds.</summary>
        private static DateTimeOffset BindDateTime(Literal value) =>
            DateTimeOffset.FromUnixTimeMilliseconds((long)value.Number);

        private bool TryResolveColumn(FieldRef field, out int index, out AttributeKind kind)
        {
            // A group row resolves through the caller's own operands, not through
            // a schema: its names are the group key's and the statistics' result
            // names, and the value compared is the reduced one (ADR-0128).
            if (_resolve is { } resolve)
            {
                if (resolve(field) is { } group)
                {
                    _group = group;
                    index = 0;
                    kind = group.Kind;
                    return true;
                }

                Error = $"the group row has no '{field.Name}' to filter on.";
                index = -1;
                kind = default;
                return false;
            }

            if (_schema is not null)
            {
                index = _schema.IndexOf(field.Name);
                if (index >= 0)
                {
                    kind = _schema[index].Kind;
                    return true;
                }

                var fields = string.Join(", ", _schema.Fields.Select(known => $"'{known.Name}'"));
                Error = $"the filter column '{field.Name}' is not a field of this dataset; available fields: {fields}.";
            }
            else
            {
                Error = "no schema is available to resolve filter columns.";
            }

            index = -1;
            kind = default;
            return false;
        }

        /// <summary>
        /// The filter column as a comparison operand: the same rendering an
        /// <c>ORDER BY</c> term gets, so a string comparison in a
        /// <c>WHERE</c> states the byte order the contract compares strings in
        /// instead of inheriting the database's collation (ADR-0123). A column
        /// of another kind takes no term — <c>COLLATE</c> is a string
        /// operator — and neither does one in a database that already compares
        /// by bytes (ADR-0121).
        /// </summary>
        private string Column(string name) =>
            _schema is null ? Quote(name) : PostgisPlanQueries.Ordered(name, _schema, _text);

        /// <summary>
        /// The operand a clause compares: the group row's own SQL where the
        /// clause is over reduced groups, and the filtered column under the byte
        /// order where the clause is over a dataset's rows.
        /// </summary>
        private string Operand(int index) =>
            _resolve is not null && _group is { } group ? group.Sql : Column(_schema![index].Name);

        /// <summary>
        /// The operand of a folded comparison: the group row's own SQL where the
        /// clause is over reduced groups, and the quoted column where the clause
        /// is over a dataset's rows — the column <em>without</em> the byte-order
        /// term, because this comparison states its own fold instead.
        /// </summary>
        private string FoldedOperand(int index) =>
            _resolve is not null && _group is { } group ? group.Sql : Quote(_schema![index].Name);

        /// <summary>The identifier quoting of the PostGIS dialect (a discovered or schema-validated name).</summary>
        private static string Quote(string name) => $"\"{name}\"";

        private static string SqlOperator(ComparisonOperator comparison) => comparison switch
        {
            ComparisonOperator.Equals => "=",
            ComparisonOperator.NotEquals => "!=",
            ComparisonOperator.LessThan => "<",
            ComparisonOperator.LessOrEqual => "<=",
            ComparisonOperator.GreaterThan => ">",
            ComparisonOperator.GreaterOrEqual => ">=",
            ComparisonOperator.Like => "LIKE",
            _ => string.Empty,
        };

        private string Parameter(object? value)
        {
            _parameters.Add(value);
            return $"@p{_parameterIndex++}";
        }
    }
}
