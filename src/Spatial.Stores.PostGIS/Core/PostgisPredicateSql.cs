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
/// </summary>
internal static class PostgisPredicateSql
{
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
        return Combine(prefilter, filter);
    }

    /// <summary>Builds the attribute fragment, or fails with an actionable error for an unknown or geometry field.</summary>
    public static string Where(Predicate where, IFeatureSchema schema, List<object?> parameters)
    {
        var builder = new SqlBuilder(schema, parameters);
        builder.Visit(where);
        return builder.Error is { } error ? throw SpatialException.BadArguments(error) : builder.ToString();
    }

    /// <summary>Builds the bounded-box spatial predicate and appends its bound values.</summary>
    public static string? BoundingBox(
        Spatial.Contracts.BoundingBox? bbox, DatasetDescription description, List<object?> parameters)
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

            _sql.Append(Quote(_schema![index].Name))
                .Append(' ')
                .Append(SqlOperator(compare.Operator))
                .Append(' ')
                .Append(Parameter(Bind(compare.Value)));
        }

        private void VisitIsNull(Predicate.IsNull isNull)
        {
            if (!TryResolveColumn(isNull.Field, out var index, out _))
            {
                return;
            }

            _sql.Append(Quote(_schema![index].Name)).Append(" IS ")
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

            if (isIn.Values.Any(value => value.Kind == LiteralKind.Null))
            {
                // `x IN (1, NULL)` and `x NOT IN (1, NULL)` are both unanswerable
                // in SQL, and the grammar refuses a NULL inside IN, so this is
                // only a hand-built plan: answer it the way SQL's three-valued
                // logic reads as "not matched".
                _sql.Append("FALSE");
                return;
            }

            _sql.Append(Quote(_schema![index].Name))
                .Append(isIn.Negated ? " NOT IN (" : " IN (");
            for (var i = 0; i < isIn.Values.Count; i++)
            {
                if (i > 0)
                {
                    _sql.Append(", ");
                }

                _sql.Append(Parameter(Bind(isIn.Values[i])));
            }

            _sql.Append(')');
        }

        private static string GeometryHint(FieldRef field) =>
            $"the filter column '{field.Name}' is a geometry field; filter spatially with the bounding box (minx/miny/maxx/maxy) instead.";

        /// <summary>
        /// The bound value of a literal: a string, a whole number carried
        /// verbatim, a double, a bool or a UTC instant for a date-time. The
        /// parameter's CLR type is what the driver binds, so nothing is
        /// re-parsed on the server.
        /// </summary>
        private static object Bind(Literal value) => value.Kind switch
        {
            LiteralKind.String => value.Text ?? string.Empty,
            LiteralKind.Integer when long.TryParse(value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => integer,
            LiteralKind.Integer => value.Number,
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
