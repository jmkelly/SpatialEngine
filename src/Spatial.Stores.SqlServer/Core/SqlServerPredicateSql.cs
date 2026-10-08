using System.Globalization;
using System.Text;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.SqlServer.Geometry;

namespace Spatial.Stores.SqlServer.Core;

/// <summary>
/// Compiles a <see cref="Predicate"/> into one fully parameterised T-SQL
/// <c>WHERE</c> fragment (ADR-0074 §3-4, ADR-0028 as the SQL Server provider
/// follows it). Fields resolve against the dataset's discovered
/// <see cref="FeatureSchema"/> — an unknown name is a typed invalid-argument
/// failure naming the available fields, and a geometry field is rejected with
/// a hint to filter spatially with the bounding box instead. Every literal
/// becomes one named parameter (<c>@p0</c>, <c>@p1</c>, …) whose value is
/// appended to <c>parameters</c> in order, so the SQL never contains a
/// client-supplied value. The grammar itself is parsed once at the boundary
/// (<c>Spatial.Core.Features.Query.FeatureFilterText</c>); this is the thin
/// T-SQL half of the back end, and the bracket quoting, the <c>&lt;&gt;</c>
/// spelling and the envelope test are the whole of its dialect.
///
/// <para>
/// Every string comparison it writes carries
/// <see cref="ByteOrderCollation"/>, so a pushed <c>WHERE</c> cannot inherit
/// the database's collation where the contract compares strings by bytes
/// (ADR-0098 §3, ADR-0123). The term is not conditional: a SQL Server
/// database's default collation is a linguistic one — the shipped default is
/// case-insensitive — and nothing in the store's own metadata says which, so
/// the one comparison that is always the contract's is the one always written.
/// </para>
///
/// <para>
/// The folded pattern states its own case behaviour instead of inheriting one
/// (ADR-0132): T-SQL has no ASCII fold of its own — <c>LOWER</c> is the
/// server's linguistic case, and a case-insensitive collation folds
/// <c>Épsilon</c> onto <c>epsilon</c>, which the reference evaluator does not do
/// — so it writes the fold itself and carries the same binary collation the
/// comparison then matches bytes in.
/// </para>
/// </summary>
internal static class SqlServerPredicateSql
{
    /// <summary>
    /// The binary collation a pushed string comparison is written under: it
    /// compares code points rather than letters, which is the nearest thing
    /// T-SQL has to the contract's byte comparison and the only one of the
    /// collations SQL Server ships that is not case-folding. It is the same
    /// answer the Postgres side states as <c>COLLATE "C"</c>, and it follows
    /// the <c>ORDER BY</c> pushdown's own argument (ADR-0121).
    /// </summary>
    public const string ByteOrderCollation = "Latin1_General_100_BIN2";

    /// <summary>The alphabet a folded pattern folds: <c>A</c>–<c>Z</c>.</summary>
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>The alphabet a folded pattern folds to: <c>a</c>–<c>z</c>.</summary>
    private const string Lower = "abcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// The expression that folds an operand over the ASCII alphabet (ADR-0132),
    /// which is the fold the reference evaluator applies to a value and a
    /// pattern before the same whole-value test runs, and which is the one fold
    /// every provider states identically. It is written out rather than reached
    /// for through <c>LOWER</c> or a case-insensitive collation because both of
    /// those are the database's: <c>LOWER</c> under a Turkish collation folds
    /// <c>I</c> to a dotless <c>ı</c>, and <c>Latin1_General_100_CI_AS</c> folds
    /// case pairs outside the ASCII alphabet, so a plan would answer differently
    /// on two deployments of the same store. The two alphabets are engine text,
    /// not client text, and every literal still binds as a parameter (ADR-0028).
    /// </summary>
    public static string Fold(string expression) => $"TRANSLATE({expression}, N'{Upper}', N'{Lower}')";

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
        List<object?> parameters)
    {
        var prefilter = BoundingBox(bbox, description, parameters);
        var filter = where is null ? null : Where(where, description.Schema, parameters);
        return (prefilter, filter) switch
        {
            (null, null) => null,
            (null, _) => filter,
            (_, null) => prefilter,
            _ => $"({prefilter}) AND ({filter})",
        };
    }

    /// <summary>Builds the attribute fragment, or fails with an actionable error for an unknown or geometry field.</summary>
    public static string Where(Predicate where, IFeatureSchema schema, List<object?> parameters)
    {
        var builder = new SqlBuilder(schema, parameters);
        builder.Visit(where);
        return builder.Error is { } error ? throw SpatialException.BadArguments(error) : builder.ToString();
    }

    /// <summary>
    /// The envelope test: the dataset's geometry column against a WKB
    /// polygon built by the interchange and bound as one parameter. The SRID
    /// is a discovered integer and the envelope is geometry, so the predicate
    /// stays fully parameterised.
    /// </summary>
    public static string? BoundingBox(
        BoundingBox? bbox, DatasetDescription description, List<object?> parameters)
    {
        if (bbox is null)
        {
            return null;
        }

        var builder = new SqlBuilder(null!, parameters);
        return builder.AppendBoundingBox(
            description.GeometryColumn,
            description.Srid,
            bbox.MinX,
            bbox.MinY,
            bbox.MaxX,
            bbox.MaxY);
    }

    private sealed class SqlBuilder
    {
        private readonly IFeatureSchema? _schema;
        private readonly List<object?> _parameters;
        private readonly StringBuilder _sql = new();
        private int _parameterIndex;

        public SqlBuilder(IFeatureSchema? schema, List<object?> parameters)
        {
            _schema = schema;
            _parameters = parameters;
            // Continue placeholder numbering from already-bound values so a
            // bbox predicate combined with an attribute filter never reuses
            // @p0..@pn (each literal becomes one positional parameter in order).
            _parameterIndex = parameters.Count;
        }

        public string? Error { get; private set; }

        public string AppendBoundingBox(
            string geometryColumn,
            int srid,
            double minx,
            double miny,
            double maxx,
            double maxy)
        {
            _sql.Append(SqlServerIdentifier.Quote(geometryColumn))
                .Append(".STIntersects(geometry::STGeomFromWKB(")
                .Append(Parameter(SqlServerWkb.Envelope(minx, miny, maxx, maxy)))
                .Append(", ").Append(srid).Append(")) = 1");
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
                    _sql.Append(constant.Value ? "(1 = 1)" : "(1 = 0)");
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
                _sql.Append(compare.Operator == ComparisonOperator.NotEquals ? "(1 = 1)" : "(1 = 0)");
                return;
            }

            // A LIKE is a whole-value text test, and a literal of a kind the
            // column cannot be compared with is a comparison that matches
            // nothing. Both are the reference evaluator's answer, and neither
            // is a comparison this server could answer at all.
            if (!TryBind(compare.Operator, compare.Value, kind, out var comparable))
            {
                _sql.Append("(1 = 0)");
                return;
            }

            // The folded pattern states its own case behaviour: the column under
            // the ASCII fold, matched as bytes. Both halves are needed — the
            // fold alone would leave the comparison under the database's
            // case-insensitive default, and the collation alone would leave the
            // fold to the database.
            if (PredicateCompatibility.FoldsCase(compare.Operator))
            {
                _sql.Append(Fold(SqlServerIdentifier.Quote(_schema![index].Name)))
                    .Append(" COLLATE ")
                    .Append(ByteOrderCollation)
                    .Append(" LIKE ")
                    .Append(Parameter(FoldedPattern((string)comparable!)));
                return;
            }

            _sql.Append(Column(_schema, index)).Append(' ')
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

            _sql.Append(SqlServerIdentifier.Quote(_schema![index].Name)).Append(" IS ")
                .Append(isNull.Negated ? "NOT NULL" : "NULL");
        }

        private void VisitIsIn(Predicate.IsIn isIn)
        {
            if (!TryResolveColumn(isIn.Field, out var index, out var kind))
            {
                return;
            }

            if (kind == AttributeKind.Geometry)
            {
                Error = GeometryHint(isIn.Field);
                return;
            }

            if (HasNullLiteral(isIn))
            {
                // `x IN (1, NULL)` and `x NOT IN (1, NULL)` are both
                // unanswerable in SQL, and the grammar refuses a NULL inside
                // IN, so this is only a hand-built plan: answer it the way
                // SQL's three-valued logic reads as "not matched".
                _sql.Append("(1 = 0)");
                return;
            }

            // A value the column can never equal is not an error and does not
            // poison the list — it simply is not a match, so it drops out. If
            // nothing is left the test answers the negated or plain truth value.
            // A comparable literal never binds to null, so null is the marker
            // for "this value is not a match and drops out".
            var bindable = BindableValues(isIn, kind);
            if (bindable.Length == 0)
            {
                _sql.Append(isIn.Negated ? "(1 = 1)" : "(1 = 0)");
                return;
            }

            _sql.Append(Column(_schema, index))
                .Append(isIn.Negated ? " NOT IN (" : " IN (");
            AppendInList(bindable);
            _sql.Append(')');
        }

        private static bool HasNullLiteral(Predicate.IsIn isIn)
        {
            foreach (var value in isIn.Values)
            {
                if (value.Kind == LiteralKind.Null)
                {
                    return true;
                }
            }

            return false;
        }

        private static object?[] BindableValues(Predicate.IsIn isIn, AttributeKind kind)
        {
            var bound = new List<object?>(isIn.Values.Count);
            foreach (var value in isIn.Values)
            {
                if (TryBind(ComparisonOperator.Equals, value, kind, out var single) && single is not null)
                {
                    bound.Add(single);
                }
            }

            return [.. bound];
        }

        private void AppendInList(object?[] bindable)
        {
            for (var i = 0; i < bindable.Length; i++)
            {
                if (i > 0)
                {
                    _sql.Append(", ");
                }

                _sql.Append(Parameter(bindable[i]));
            }
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
        /// carried verbatim, a double, a bool, a UTC instant for a date-time,
        /// or <see cref="DBNull"/> for an explicit NULL. The parameter's CLR
        /// type is what the driver binds, so nothing is re-parsed on the server.
        /// </summary>
        private static object Bind(Literal value)
        {
            if (value.Kind == LiteralKind.Integer)
            {
                return BindInteger(value);
            }

            return BindOther(value);
        }

        private static object BindInteger(Literal value)
        {
            if (long.TryParse(value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            {
                return integer;
            }

            return value.Number;
        }

        private static object BindOther(Literal value) => value.Kind switch
        {
            LiteralKind.String => value.Text ?? string.Empty,
            LiteralKind.Decimal => value.Number,
            LiteralKind.Boolean => value.Boolean,
            LiteralKind.DateTime => DateTimeOffset.FromUnixTimeMilliseconds((long)value.Number),
            LiteralKind.Null => DBNull.Value,
            _ => throw SpatialException.BadArguments($"the filter literal '{value.Text}' is not a bindable value."),
        };

        private bool TryResolveColumn(FieldRef field, out int index, out AttributeKind kind)
        {
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
        /// The filter column as a comparison operand. A text column carries
        /// <see cref="ByteOrderCollation"/>, because a case-insensitive
        /// collation answers a different set from the reference for
        /// <c>code &lt; 'delta'</c> and for a <c>LIKE</c> (ADR-0123); a column
        /// of another kind carries no collation, because T-SQL will not apply a
        /// text collation to a number, a date or a guid.
        /// </summary>
        private static string Column(IFeatureSchema? schema, int index)
        {
            var name = schema![index].Name;
            return schema[index].Kind == AttributeKind.String
                ? $"{SqlServerIdentifier.Quote(name)} COLLATE {ByteOrderCollation}"
                : SqlServerIdentifier.Quote(name);
        }

        private static string SqlOperator(ComparisonOperator comparison) => comparison switch
        {
            ComparisonOperator.Equals => "=",
            ComparisonOperator.NotEquals => "<>",
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

/// <summary>
/// Quotes a discovered identifier with SQL Server's brackets. Safe because
/// every name reaching SQL has already been validated or discovered, and the
/// validator refuses the one character a quoted identifier cannot carry.
/// </summary>
internal static class SqlServerIdentifier
{
    public static string Quote(string name) => $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";
}
