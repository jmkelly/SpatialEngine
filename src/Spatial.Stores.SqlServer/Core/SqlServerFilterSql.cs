using System.Globalization;
using System.Text;
using Spatial.Core.Features;
using Spatial.Stores.SqlServer.Geometry;

namespace Spatial.Stores.SqlServer.Core;

/// <summary>
/// Turns a parsed filter expression into a <c>WHERE</c> fragment with bound
/// parameter values (ADR-0028 parameterised attribute filtering, as the
/// SQL Server provider follows it). Columns must resolve against the
/// dataset's discovered <see cref="FeatureSchema"/> — unknown columns are
/// invalid arguments naming the available fields, and geometry columns are
/// rejected with a hint to filter spatially via the bounding box instead.
/// Every literal becomes one named parameter (<c>@p0</c>, <c>@p1</c>, …)
/// whose value is appended to the <paramref name="parameters"/> list in
/// order; the SQL never contains a client-supplied literal. Built fragments
/// are deterministic for equal trees.
/// </summary>
internal static class SqlServerFilterSql
{
    /// <summary>
    /// Builds the WHERE fragment (without the <c>WHERE</c> keyword) for one
    /// parsed filter. Returns false with an actionable error when a column is
    /// unknown or a literal cannot be bound.
    /// </summary>
    public static bool TryBuild(
        FilterExpression expression,
        IFeatureSchema schema,
        List<object?> parameters,
        out string sql,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(parameters);

        var builder = new SqlBuilder(schema, parameters);
        builder.Visit(expression);
        if (builder.Error is not null)
        {
            sql = string.Empty;
            error = builder.Error;
            return false;
        }

        sql = builder.ToString();
        error = string.Empty;
        return true;
    }

    /// <summary>Builds the bounded-box spatial predicate and appends its bound envelope bytes.</summary>
    public static string BoundingBox(
        BoundingBox bounds,
        string geometryColumn,
        int srid,
        List<object?> parameters)
    {
        var builder = new SqlBuilder(null!, parameters);
        return builder.AppendBoundingBox(
            geometryColumn,
            srid,
            bounds.MinX,
            bounds.MinY,
            bounds.MaxX,
            bounds.MaxY);
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

        /// <summary>
        /// The envelope test: the dataset's geometry column against a WKB
        /// polygon built by the interchange and bound as one parameter. The
        /// SRID is a discovered integer, and the envelope is geometry, so the
        /// predicate stays fully parameterised.
        /// </summary>
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

        public void Visit(FilterExpression expression)
        {
            switch (expression)
            {
                case FilterExpression.And and:
                    VisitTerms(and.Terms, "AND");
                    break;
                case FilterExpression.Or or:
                    VisitTerms(or.Terms, "OR");
                    break;
                case FilterExpression.Comparison comparison:
                    VisitComparison(comparison);
                    break;
                case FilterExpression.IsNull isNull:
                    VisitIsNull(isNull);
                    break;
            }
        }

        public override string ToString() => _sql.ToString();

        private void VisitTerms(IReadOnlyList<FilterExpression> terms, string conjunction)
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
        private static bool NeedsGrouping(FilterExpression term, string conjunction) => term switch
        {
            FilterExpression.And group when conjunction == "OR" => group.Terms.Count > 1,
            FilterExpression.Or group when conjunction == "AND" => group.Terms.Count > 1,
            _ => false,
        };

        private void VisitComparison(FilterExpression.Comparison comparison)
        {
            if (!TryResolveColumn(comparison.Column, out var index, out var kind))
            {
                return;
            }

            if (kind == AttributeKind.Geometry)
            {
                Error = $"the filter column '{comparison.Column}' is a geometry field; filter spatially with the bounding box (minx/miny/maxx/maxy) instead.";
                return;
            }

            var value = ConvertValue(comparison.Value);
            if (Error is not null)
            {
                return;
            }

            _sql.Append(SqlServerIdentifier.Quote(_schema![index].Name)).Append(' ')
                .Append(SqlOperator(comparison.Operator))
                .Append(' ')
                .Append(Parameter(value));
        }

        private void VisitIsNull(FilterExpression.IsNull isNull)
        {
            if (!TryResolveColumn(isNull.Column, out var index, out _))
            {
                return;
            }

            _sql.Append(SqlServerIdentifier.Quote(_schema![index].Name)).Append(" IS ")
                .Append(isNull.Negated ? "NOT NULL" : "NULL");
        }

        private object? ConvertValue(FilterValue value)
        {
            switch (value.Kind)
            {
                case FilterValueKind.Boolean:
                    return bool.Parse(value.Text);
                case FilterValueKind.String:
                    return value.Text;
                case FilterValueKind.Integer when long.TryParse(value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer):
                    return integer;
                case FilterValueKind.Decimal when double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number):
                    return number;
                default:
                    Error = $"the filter literal '{value.Text}' is not a valid {ValueName(value.Kind)}.";
                    return null;
            }
        }

        /// <summary>The human kind name for a literal (used in invalid-literal errors).</summary>
        private static readonly Dictionary<FilterValueKind, string> ValueNames = new()
        {
            [FilterValueKind.Integer] = "integer",
            [FilterValueKind.Decimal] = "number",
            [FilterValueKind.Boolean] = "boolean",
        };

        private static string ValueName(FilterValueKind kind) =>
            ValueNames.TryGetValue(kind, out var name) ? name : "string";

        private bool TryResolveColumn(string column, out int index, out AttributeKind kind)
        {
            if (_schema is not null)
            {
                index = _schema.IndexOf(column);
                if (index >= 0)
                {
                    kind = _schema[index].Kind;
                    return true;
                }

                var fields = string.Join(", ", _schema.Fields.Select(field => $"'{field.Name}'"));
                Error = $"the filter column '{column}' is not a field of this dataset; available fields: {fields}.";
            }
            else
            {
                Error = "no schema is available to resolve filter columns.";
            }

            index = -1;
            kind = default;
            return false;
        }

        private static string SqlOperator(FilterOperator filterOperator) => filterOperator switch
        {
            FilterOperator.Equals => "=",
            FilterOperator.NotEquals => "<>",
            FilterOperator.LessThan => "<",
            FilterOperator.LessOrEqual => "<=",
            FilterOperator.GreaterThan => ">",
            FilterOperator.GreaterOrEqual => ">=",
            FilterOperator.Like => "LIKE",
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

/// <summary>The validated bounding-box bounds of a query invocation (min &lt;= max, finite).</summary>
internal readonly record struct BoundingBox(double MinX, double MinY, double MaxX, double MaxY);
