namespace Spatial.Stores.PostGIS.Core;

/// <summary>
/// The attribute filter model of feature queries (ADR-0028,
/// architecture/distilled/contracts.md): a small, fully-parameterised expression
/// language over a dataset's fields. Consumers of the AST never see SQL or
/// literals inlined anywhere — <see cref="PostgisFilterSql"/> turns the tree
/// into a WHERE fragment with bound parameters, and column identifiers are
/// resolved against the dataset's discovered schema (unknown names are
/// invalid arguments; geometry columns are rejected with a hint to use the
/// bounding box).
/// </summary>
internal abstract record FilterExpression
{
    /// <summary>Conjunction (<c>a AND b AND c</c>).</summary>
    public sealed record And(IReadOnlyList<FilterExpression> Terms) : FilterExpression;

    /// <summary>Disjunction (<c>a OR b OR c</c>).</summary>
    public sealed record Or(IReadOnlyList<FilterExpression> Terms) : FilterExpression;

    /// <summary>One comparison (<c>column = value</c>) or pattern test (<c>column LIKE value</c>).</summary>
    public sealed record Comparison(string Column, FilterOperator Operator, FilterValue Value) : FilterExpression;

    /// <summary>Null test (<c>column IS [NOT] NULL</c>).</summary>
    public sealed record IsNull(string Column, bool Negated) : FilterExpression;
}

/// <summary>The comparison operators the filter language accepts.</summary>
internal enum FilterOperator
{
    Equals,
    NotEquals,
    LessThan,
    LessOrEqual,
    GreaterThan,
    GreaterOrEqual,
    Like,
}

/// <summary>A literal filter value; the SQL builder converts it to a bound parameter.</summary>
internal readonly record struct FilterValue(FilterValueKind Kind, string Text);

/// <summary>The literal kinds the lexer can produce.</summary>
internal enum FilterValueKind
{
    String,
    Integer,
    Decimal,
    Boolean,
}
