using System.Globalization;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>The result types a MapLibre expression can yield.</summary>
internal enum ExpressionType
{
    /// <summary>No value: the expression is undefined for this feature or input.</summary>
    Missing,

    Number,
    Text,
    Flag,
    Color,

    /// <summary>Not known until evaluation (<c>get</c>, <c>id</c>, <c>var</c>, mixed operators).</summary>
    Any,
}

/// <summary>
/// One evaluated expression value. The dialect is typed, so a value carries
/// its <see cref="ExpressionType"/> and the readers that bind a value to a
/// paint property check it rather than coercing silently.
/// </summary>
internal readonly record struct ExpressionValue
{
    private ExpressionValue(ExpressionType type) => Type = type;

    /// <summary>The absence of a value, which a paint property falls back from.</summary>
    public static ExpressionValue Missing { get; } = new(ExpressionType.Missing);

    public ExpressionType Type { get; }

    public double Number { get; init; }

    public string Text { get; init; } = string.Empty;

    public bool Flag { get; init; }

    public StyleColor Color { get; init; }

    public static ExpressionValue OfNumber(double value) => new(ExpressionType.Number) { Number = value };

    public static ExpressionValue OfText(string value) => new(ExpressionType.Text) { Text = value };

    public static ExpressionValue OfFlag(bool value) => new(ExpressionType.Flag) { Flag = value };

    public static ExpressionValue OfColor(StyleColor value) => new(ExpressionType.Color) { Color = value };

    public static ExpressionValue From(string? value) =>
        value is null ? Missing : OfText(value);

    /// <summary>Whether the value is absent, which every operator short-circuits over.</summary>
    public bool IsMissing => Type == ExpressionType.Missing;

    /// <summary>The numeric value; non-numbers read as <c>0</c> (operators never reach here mismatched).</summary>
    public double AsNumber() => Type == ExpressionType.Number ? Number : 0;

    public string AsText() => Type == ExpressionType.Text ? Text : string.Empty;

    /// <summary>The boolean value; anything that is not a flag reads as <c>false</c>.</summary>
    public bool AsFlag() => Type == ExpressionType.Flag && Flag;

    public override string ToString() => Type switch
    {
        ExpressionType.Number => Number.ToString(CultureInfo.InvariantCulture),
        ExpressionType.Text => Text,
        ExpressionType.Flag => Flag ? "true" : "false",
        ExpressionType.Color => Color.ToHex(),
        _ => "null",
    };
}
