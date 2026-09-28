namespace Spatial.Core.Features.Query;

/// <summary>
/// The one predicate vocabulary of feature queries (ADR-0074 §2): a small
/// tree of conjunctions, disjunctions, comparisons, null tests, membership
/// tests and constants over a <see cref="FieldRef"/> and a
/// <see cref="Literal"/>. It is a <em>value</em> — it lives in Core next to
/// <see cref="FeatureSchema"/> because it is the same kind of structural
/// vocabulary, and Core holds no evaluation: a store compiles the tree to its
/// own dialect or evaluates it in memory, never both meanings at once.
/// </summary>
public abstract record Predicate
{
    private Predicate()
    {
    }

    /// <summary>Conjunction: every term must hold.</summary>
    public sealed record Every(IReadOnlyList<Predicate> Terms) : Predicate;

    /// <summary>Disjunction: at least one term must hold.</summary>
    public sealed record Some(IReadOnlyList<Predicate> Terms) : Predicate;

    /// <summary>One comparison of a field against a literal (or a LIKE pattern test).</summary>
    public sealed record Compare(FieldRef Field, ComparisonOperator Operator, Literal Value) : Predicate;

    /// <summary>A null test (<c>IS [NOT] NULL</c>).</summary>
    public sealed record IsNull(FieldRef Field, bool Negated) : Predicate;

    /// <summary>A membership test (<c>[NOT] IN (…)</c>) over bound literals.</summary>
    public sealed record IsIn(FieldRef Field, IReadOnlyList<Literal> Values, bool Negated) : Predicate;

    /// <summary>
    /// A field-independent predicate whose truth is fixed when the text is
    /// parsed, such as the Esri match-all <c>1=1</c> and <c>1=0</c>. It
    /// references no field, so it neither reads a feature nor a column.
    /// </summary>
    public sealed record Constant(bool Value) : Predicate;

    /// <summary>The predicate that holds for every feature (the Esri <c>1=1</c>).</summary>
    public static Predicate All { get; } = new Constant(true);

    /// <summary>The predicate that holds for no feature (the Esri <c>1=0</c>).</summary>
    public static Predicate None { get; } = new Constant(false);

    /// <summary>
    /// The fields the predicate reads, in first-appearance order. A
    /// <see cref="Constant"/> references none. Facades use this to validate a
    /// clause against a layer schema (the Esri <c>validateSQL</c> face).
    /// </summary>
    public IReadOnlyList<FieldRef> Fields() =>
        Walk(this).Select(term => term switch
        {
            Compare compare => compare.Field,
            IsNull isNull => isNull.Field,
            IsIn isIn => isIn.Field,
            _ => null,
        })
        .OfType<FieldRef>()
        .Distinct()
        .ToArray();

    /// <summary>Every node of the tree, parents before children.</summary>
    private static IEnumerable<Predicate> Walk(Predicate predicate)
    {
        yield return predicate;
        foreach (var term in Children(predicate))
        {
            foreach (var node in Walk(term))
            {
                yield return node;
            }
        }
    }

    private static IReadOnlyList<Predicate> Children(Predicate predicate) => predicate switch
    {
        Every every => every.Terms,
        Some some => some.Terms,
        _ => [],
    };
}

/// <summary>A reference to one dataset field, by name (never by position).</summary>
public sealed record FieldRef(string Name)
{
    /// <summary>Whether the reference names <paramref name="name"/> exactly (the schema lookup is ordinal).</summary>
    public bool Is(string name) => string.Equals(Name, name, StringComparison.Ordinal);
}

/// <summary>The comparison operators the vocabulary supports.</summary>
public enum ComparisonOperator
{
    /// <summary><c>=</c></summary>
    Equals,

    /// <summary><c>!=</c> / <c>&lt;&gt;</c></summary>
    NotEquals,

    /// <summary><c>&lt;</c></summary>
    LessThan,

    /// <summary><c>&lt;=</c></summary>
    LessOrEqual,

    /// <summary><c>&gt;</c></summary>
    GreaterThan,

    /// <summary><c>&gt;=</c></summary>
    GreaterOrEqual,

    /// <summary><c>LIKE</c> — the pattern wildcards are <c>%</c> and <c>_</c>.</summary>
    Like,
}

/// <summary>The literal kinds the vocabulary supports.</summary>
public enum LiteralKind
{
    /// <summary>A quoted string (or a <c>LIKE</c> pattern).</summary>
    String,

    /// <summary>A whole number, carried verbatim in <see cref="Literal.Text"/> so no precision is lost.</summary>
    Integer,

    /// <summary>A fractional number, carried as a <see cref="Literal.Number"/>.</summary>
    Decimal,

    /// <summary><c>TRUE</c> / <c>FALSE</c>.</summary>
    Boolean,

    /// <summary>An explicit <c>NULL</c> literal.</summary>
    Null,

    /// <summary>
    /// A date-time (<c>TIMESTAMP '…'</c> or <c>CURRENT_TIMESTAMP …</c>):
    /// epoch milliseconds in <see cref="Literal.Number"/>, which every back end
    /// compares in the same instant-based way.
    /// </summary>
    DateTime,
}

/// <summary>One literal value in a <see cref="Predicate"/>.</summary>
public readonly record struct Literal(LiteralKind Kind, string? Text, double Number, bool Boolean)
{
    /// <summary>A string literal.</summary>
    public static Literal FromText(string value) => new(LiteralKind.String, value, 0, false);

    /// <summary>An integer literal, carried verbatim.</summary>
    public static Literal FromInteger(string text) => new(LiteralKind.Integer, text, 0, false);

    /// <summary>A fractional-number literal.</summary>
    public static Literal FromNumber(double value) => new(LiteralKind.Decimal, null, value, false);

    /// <summary>A boolean literal.</summary>
    public static Literal FromBoolean(bool value) => new(LiteralKind.Boolean, null, 0, value);

    /// <summary>A date-time literal, in epoch milliseconds.</summary>
    public static Literal FromMilliseconds(long milliseconds) => new(LiteralKind.DateTime, null, milliseconds, false);

    /// <summary>The <c>NULL</c> literal.</summary>
    public static Literal Null { get; } = new(LiteralKind.Null, null, 0, false);
}
