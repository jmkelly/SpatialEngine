using System.Text;
using Spatial.Contracts;
using Spatial.Core.Geometry;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>
/// A compiled MapLibre expression: an immutable node evaluated against one
/// <see cref="ExpressionScope"/> (a feature, its geometry type and the
/// viewport zoom). The node set is closed — an unknown operator is a typed
/// <c>invalid.arguments</c> at compile time, never a runtime surprise — and
/// every node is a pure function of the scope, so a node that mentions no
/// <c>var</c> can be memoised per feature (see <see cref="ExpressionScope"/>).
/// </summary>
internal abstract record StyleExpression
{
    /// <summary>The type every evaluation of this node yields.</summary>
    public abstract ExpressionType Type { get; }

    /// <summary>Whether the node reads a <c>let</c> binding, which makes it scope-dependent.</summary>
    public virtual bool UsesVariables => false;

    /// <summary>The operand nodes, so a compile-time pass can walk the tree.</summary>
    public virtual IEnumerable<StyleExpression> Children => [];

    public abstract ExpressionValue Evaluate(ExpressionScope scope);
}

/// <summary>A literal value (or the <c>["literal", …]</c> form).</summary>
internal sealed record LiteralExpression(ExpressionValue Value) : StyleExpression
{
    public override ExpressionType Type => Value.Type;

    public override ExpressionValue Evaluate(ExpressionScope scope) => Value;
}

/// <summary><c>["zoom"]</c>: the viewport's zoom level.</summary>
internal sealed record ZoomExpression : StyleExpression
{
    public override ExpressionType Type => ExpressionType.Number;

    public override ExpressionValue Evaluate(ExpressionScope scope) => ExpressionValue.OfNumber(scope.Zoom);
}

/// <summary><c>["id"]</c>: the feature identity.</summary>
internal sealed record IdentityExpression : StyleExpression
{
    public override ExpressionType Type => ExpressionType.Text;

    public override ExpressionValue Evaluate(ExpressionScope scope) => ExpressionValue.From(scope.FeatureId);
}

/// <summary><c>["geometry-type"]</c>: the feature geometry's MapLibre type name.</summary>
internal sealed record GeometryTypeExpression : StyleExpression
{
    public override ExpressionType Type => ExpressionType.Text;

    public override ExpressionValue Evaluate(ExpressionScope scope) => ExpressionValue.OfText(scope.GeometryType);
}

/// <summary><c>["get", name]</c> or <c>["get", name, fallback]</c> over feature attributes.</summary>
internal sealed record AttributeExpression(string Field, ExpressionValue Fallback) : StyleExpression
{
    public override ExpressionType Type => ExpressionType.Any;

    public override ExpressionValue Evaluate(ExpressionScope scope) => scope.Attribute(Field, Fallback);
}

/// <summary><c>["has", name]</c>: whether the attribute is present and not null.</summary>
internal sealed record PresenceExpression(string Field) : StyleExpression
{
    public override ExpressionType Type => ExpressionType.Flag;

    public override ExpressionValue Evaluate(ExpressionScope scope) =>
        ExpressionValue.OfFlag(!scope.Attribute(Field, ExpressionValue.Missing).IsMissing);
}

/// <summary><c>["var", name]</c>: a <c>let</c> binding.</summary>
internal sealed record VariableExpression(string Name) : StyleExpression
{
    public override ExpressionType Type => ExpressionType.Any;

    public override bool UsesVariables => true;

    public override ExpressionValue Evaluate(ExpressionScope scope) => scope.Variable(Name);
}

/// <summary><c>["let", name, value, …, result]</c>.</summary>
internal sealed record LetExpression(
    IReadOnlyList<KeyValuePair<string, StyleExpression>> Bindings,
    StyleExpression Result) : StyleExpression
{
    public override ExpressionType Type => Result.Type;


    public override bool UsesVariables => true;

    public override ExpressionValue Evaluate(ExpressionScope scope)
    {
        scope.PushBindings(Bindings);
        try
        {
            return scope.Evaluate(Result);
        }
        finally
        {
            scope.PopBindings(Bindings.Count);
        }
    }
}

/// <summary>Comparison operators (<c>==</c>, <c>!=</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>).</summary>
internal sealed record ComparisonExpression(
    string Operator, StyleExpression Left, StyleExpression Right) : StyleExpression
{
    public override ExpressionType Type => ExpressionType.Flag;

    public override IEnumerable<StyleExpression> Children => [Left, Right];

    public override ExpressionValue Evaluate(ExpressionScope scope)
    {
        var left = scope.Evaluate(Left);
        var right = scope.Evaluate(Right);
        return left.IsMissing || right.IsMissing
            ? ExpressionValue.OfFlag(false)
            : ExpressionValue.OfFlag(ExpressionValues.Compare(Operator, left, right));
    }
}

/// <summary><c>["all", …]</c> and <c>["any", …]</c>, which short-circuit.</summary>
internal sealed record LogicalExpression(string Operator, IReadOnlyList<StyleExpression> Operands) : StyleExpression
{
    public override ExpressionType Type => ExpressionType.Flag;

    public override IEnumerable<StyleExpression> Children => Operands;

    public override ExpressionValue Evaluate(ExpressionScope scope)
    {
        foreach (var operand in Operands)
        {
            var flag = scope.Evaluate(operand).AsFlag();
            if (Operator == "all" && !flag)
            {
                return ExpressionValue.OfFlag(false);
            }

            if (Operator == "any" && flag)
            {
                return ExpressionValue.OfFlag(true);
            }
        }

        return ExpressionValue.OfFlag(Operator != "any");
    }
}

/// <summary><c>["!", operand]</c>.</summary>
internal sealed record NegationExpression(StyleExpression Operand) : StyleExpression
{
    public override ExpressionType Type => ExpressionType.Flag;

    public override IEnumerable<StyleExpression> Children => [Operand];

    public override ExpressionValue Evaluate(ExpressionScope scope) =>
        ExpressionValue.OfFlag(!scope.Evaluate(Operand).AsFlag());
}

/// <summary>
/// <c>["in", needle, haystack, …]</c>. A string haystack is the MapLibre
/// substring form; the extra literal candidates are the "one of" form.
/// </summary>
internal sealed record MembershipExpression(
    StyleExpression Needle, StyleExpression Haystack, IReadOnlyList<ExpressionValue> Extras) : StyleExpression
{
    public override ExpressionType Type => ExpressionType.Flag;

    public override IEnumerable<StyleExpression> Children => [Needle, Haystack];

    public override ExpressionValue Evaluate(ExpressionScope scope)
    {
        var needle = scope.Evaluate(Needle);
        if (needle.IsMissing)
        {
            return ExpressionValue.OfFlag(false);
        }

        var haystack = scope.Evaluate(Haystack);
        if (!haystack.IsMissing && haystack.AsText().Contains(needle.ToString(), StringComparison.Ordinal))
        {
            return ExpressionValue.OfFlag(true);
        }

        var found = false;
        foreach (var candidate in Extras)
        {
            found = found || ExpressionValues.Compare("==", needle, candidate);
        }

        return ExpressionValue.OfFlag(found);
    }
}

/// <summary><c>["concat", …]</c> over string-convertible operands.</summary>
internal sealed record ConcatExpression(IReadOnlyList<StyleExpression> Operands) : StyleExpression
{
    public override ExpressionType Type => ExpressionType.Text;

    public override IEnumerable<StyleExpression> Children => Operands;

    public override ExpressionValue Evaluate(ExpressionScope scope)
    {
        var builder = new StringBuilder();
        foreach (var operand in Operands)
        {
            var value = scope.Evaluate(operand);
            if (!value.IsMissing)
            {
                builder.Append(value);
            }
        }

        return ExpressionValue.OfText(builder.ToString());
    }
}

/// <summary><c>["case", condition, output, …, fallback]</c>.</summary>
internal sealed record CaseExpression(
    IReadOnlyList<KeyValuePair<StyleExpression, StyleExpression>> Branches,
    StyleExpression Fallback) : StyleExpression
{
    public override ExpressionType Type => ExpressionTypes.Unify(branches: Branches, Fallback);

    public override IEnumerable<StyleExpression> Children =>
        Branches.SelectMany(branch => new[] { branch.Key, branch.Value }).Append(Fallback);

    public override ExpressionValue Evaluate(ExpressionScope scope)
    {
        foreach (var branch in Branches)
        {
            if (scope.Evaluate(branch.Key).AsFlag())
            {
                return scope.Evaluate(branch.Value);
            }
        }

        return scope.Evaluate(Fallback);
    }
}

/// <summary>One <c>match</c> arm: its label (or label list) and its output.</summary>
internal sealed record MatchArm(IReadOnlyList<ExpressionValue> Labels, StyleExpression Output);

/// <summary><c>["match", input, label, output, …, fallback]</c>.</summary>
internal sealed record MatchExpression(
    StyleExpression Input, IReadOnlyList<MatchArm> Arms, StyleExpression Fallback) : StyleExpression
{
    public override ExpressionType Type => ExpressionTypes.Unify(Arms.Select(arm => arm.Output).Append(Fallback));

    public override IEnumerable<StyleExpression> Children =>
        Arms.Select(arm => arm.Output).Prepend(Input).Append(Fallback);

    public override ExpressionValue Evaluate(ExpressionScope scope)
    {
        var input = scope.Evaluate(Input);
        foreach (var arm in Arms)
        {
            if (arm.Labels.Any(label => !input.IsMissing && ExpressionValues.Compare("==", input, label)))
            {
                return scope.Evaluate(arm.Output);
            }
        }

        return scope.Evaluate(Fallback);
    }
}

/// <summary><c>["coalesce", …]</c>: the first operand that is not missing.</summary>
internal sealed record CoalesceExpression(IReadOnlyList<StyleExpression> Operands) : StyleExpression
{
    public override ExpressionType Type => ExpressionTypes.Unify(Operands);

    public override IEnumerable<StyleExpression> Children => Operands;

    public override ExpressionValue Evaluate(ExpressionScope scope)
    {
        foreach (var operand in Operands)
        {
            var value = scope.Evaluate(operand);
            if (!value.IsMissing)
            {
                return value;
            }
        }

        return ExpressionValue.Missing;
    }
}

/// <summary><c>["step", input, default, stop, output, …]</c>.</summary>
internal sealed record StepExpression(
    StyleExpression Input,
    StyleExpression Default,
    IReadOnlyList<KeyValuePair<double, StyleExpression>> Stops) : StyleExpression
{
    public override ExpressionType Type => ExpressionTypes.Unify(Stops.Select(stop => stop.Value).Append(Default));

    public override IEnumerable<StyleExpression> Children =>
        Stops.Select(stop => stop.Value).Prepend(Input).Append(Default);

    public override ExpressionValue Evaluate(ExpressionScope scope)
    {
        var input = scope.Evaluate(Input);
        if (input.IsMissing)
        {
            return ExpressionValue.Missing;
        }

        var value = scope.Evaluate(Default);
        foreach (var stop in Stops)
        {
            if (input.AsNumber() < stop.Key)
            {
                return value;
            }

            value = scope.Evaluate(stop.Value);
        }

        return value;
    }
}

/// <summary>How <c>interpolate</c> shapes the input value before it is scaled.</summary>
internal enum InterpolationKind
{
    Linear,
    Exponential,
}

/// <summary><c>["interpolate", kind, input, stop, output, …]</c> over numbers or colours.</summary>
internal sealed record InterpolateExpression(
    StyleExpression Input,
    InterpolationKind Kind,
    double Base,
    IReadOnlyList<KeyValuePair<double, StyleExpression>> Stops) : StyleExpression
{
    /// <summary>The unified type of the stop outputs, decided once by the reader.</summary>
    public ExpressionType OutputType => ExpressionTypes.Unify(Stops.Select(stop => stop.Value));

    public override ExpressionType Type => OutputType;

    public override IEnumerable<StyleExpression> Children =>
        Stops.Select(stop => stop.Value).Prepend(Input);

    public override ExpressionValue Evaluate(ExpressionScope scope)
    {
        var input = scope.Evaluate(Input);
        if (input.IsMissing)
        {
            return ExpressionValue.Missing;
        }

        var index = Bracket(input.AsNumber());
        if (index < 0)
        {
            return scope.Evaluate(Stops[^1].Value);
        }

        if (index == 0)
        {
            return scope.Evaluate(Stops[0].Value);
        }

        var from = scope.Evaluate(Stops[index - 1].Value);
        var to = scope.Evaluate(Stops[index].Value);
        var progress = Progress(input.AsNumber(), Stops[index - 1].Key, Stops[index].Key);
        return OutputType == ExpressionType.Color
            ? ExpressionValue.OfColor(ExpressionValues.MixColor(from.Color, to.Color, progress))
            : ExpressionValue.OfNumber(from.AsNumber() + ((to.AsNumber() - from.AsNumber()) * progress));
    }

    /// <summary>The index of the first stop strictly above the value, or -1 at the top end.</summary>
    private int Bracket(double value)
    {
        for (var index = 1; index < Stops.Count; index++)
        {
            if (value < Stops[index].Key)
            {
                return index;
            }
        }

        return -1;
    }

    private double Progress(double value, double lower, double upper)
    {
        var span = upper - lower;
        if (span <= 0)
        {
            return 0;
        }

        var progress = Math.Clamp((value - lower) / span, 0, 1);
        return Kind == InterpolationKind.Exponential ? Math.Pow(progress, Base) : progress;
    }
}

/// <summary>
/// Arithmetic (<c>+</c>, <c>-</c>, <c>*</c>, <c>/</c>, <c>%</c>) and unary
/// minus. A text operand makes the arithmetic a string concatenation, which is
/// the MapLibre reading of <c>+</c> over strings.
/// </summary>
internal sealed record ArithmeticExpression(
    string Operator, StyleExpression Left, StyleExpression? Right) : StyleExpression
{
    public override ExpressionType Type =>
        Left.Type == ExpressionType.Text || Right?.Type == ExpressionType.Text ? ExpressionType.Text : ExpressionType.Number;

    public override IEnumerable<StyleExpression> Children =>
        Right is null ? [Left] : [Left, Right];

    public override ExpressionValue Evaluate(ExpressionScope scope)
    {
        var left = scope.Evaluate(Left);
        if (left.IsMissing || (Right is not null && scope.Evaluate(Right) is { IsMissing: true }))
        {
            return ExpressionValue.Missing;
        }

        if (Type == ExpressionType.Text)
        {
            return ExpressionValue.OfText(
                left + (Right is null ? string.Empty : scope.Evaluate(Right).ToString()));
        }

        return ExpressionValue.OfNumber(Apply(left.AsNumber(), Right is null ? 0 : scope.Evaluate(Right).AsNumber()));
    }

    /// <summary>Unary minus negates; the binary forms are arithmetic.</summary>
    private double Apply(double left, double right) => Right is null
        ? -left
        : Operator switch
        {
            "+" => left + right,
            "-" => left - right,
            "*" => left * right,
            "/" => left / right,
            _ => left % right,
        };
}

/// <summary>
/// A number-typed expression multiplied by a constant: the DPI scaling of a
/// data-driven size (T-045 item 3), applied to the compiled plan's own copy so
/// a cached plan is never mutated.
/// </summary>
internal sealed record ScaledExpression(StyleExpression Inner, double Factor) : StyleExpression
{
    public override ExpressionType Type => ExpressionType.Number;

    public override IEnumerable<StyleExpression> Children => [Inner];

    public override ExpressionValue Evaluate(ExpressionScope scope) =>
        ExpressionValue.OfNumber(scope.Evaluate(Inner).AsNumber() * Factor);
}

/// <summary>Value semantics shared by the operators.</summary>
internal static class ExpressionValues
{
    /// <summary>
    /// The MapLibre comparison. Numbers compare across the int/double kinds,
    /// everything else structurally; a value of another kind is incomparable
    /// and the comparison is <c>false</c> — never a render-time failure.
    /// </summary>
    public static bool Compare(string op, ExpressionValue left, ExpressionValue right)
    {
        if (left.Type == ExpressionType.Number && right.Type == ExpressionType.Number)
        {
            return Order(op, left.AsNumber() - right.AsNumber());
        }

        if (op is not ("==" or "!="))
        {
            return false;
        }

        var equal = left.Type == right.Type && left.ToString() == right.ToString();
        return op == "==" ? equal : !equal;
    }

    /// <summary>Per-channel colour interpolation, each channel rounded to a byte.</summary>
    public static StyleColor MixColor(StyleColor from, StyleColor to, double progress)
    {
        static byte Channel(byte start, byte end, double progress) =>
            (byte)Math.Clamp(Math.Round(start + ((end - start) * progress)), 0, 255);

        return new StyleColor(
            Channel(from.Red, to.Red, progress),
            Channel(from.Green, to.Green, progress),
            Channel(from.Blue, to.Blue, progress),
            Channel(from.Alpha, to.Alpha, progress));
    }

    private static bool Order(string op, double difference) => op switch
    {
        "<" => difference < 0,
        "<=" => difference <= 0,
        ">" => difference > 0,
        ">=" => difference >= 0,
        _ => difference == 0,
    };
}

/// <summary>Type inference and the compile-time property checks that use it.</summary>
internal static class ExpressionTypes
{
    /// <summary>Rejects a colour property that reads a number, naming both the property and the expression.</summary>
    public static void ExpectColor(string property, StyleExpression expression)
    {
        if (expression.Type == ExpressionType.Number)
        {
            throw Mismatch(property, "a colour", expression);
        }
    }

    /// <summary>Rejects a number property that reads a colour or text, naming both.</summary>
    public static void ExpectNumber(string property, StyleExpression expression)
    {
        if (expression.Type is ExpressionType.Color or ExpressionType.Text)
        {
            throw Mismatch(property, "a number", expression);
        }
    }

    /// <summary>Unifies an operator's outputs, or <see cref="ExpressionType.Any"/> when they disagree.</summary>
    public static ExpressionType Unify(IEnumerable<StyleExpression> operands)
    {
        ExpressionType? seen = null;
        foreach (var operand in operands)
        {
            if (operand.Type is ExpressionType.Missing or ExpressionType.Any)
            {
                return ExpressionType.Any;
            }

            if (seen is null)
            {
                seen = operand.Type;
            }
            else if (seen != operand.Type)
            {
                return ExpressionType.Any;
            }
        }

        return seen ?? ExpressionType.Missing;
    }

    /// <summary>Unifies a <c>case</c>'s branch outputs plus its fallback.</summary>
    public static ExpressionType Unify(
        IReadOnlyList<KeyValuePair<StyleExpression, StyleExpression>> branches, StyleExpression fallback) =>
        Unify(branches.Select(branch => branch.Value).Append(fallback));

    private static SpatialException Mismatch(string property, string expected, StyleExpression expression) =>
        SpatialException.BadArguments(
            $"'{property}' expects {expected}, but the expression {expression} yields {expression.Type.ToString().ToLowerInvariant()}.");
}

/// <summary>The MapLibre type name of a geometry, as <c>["geometry-type"]</c> reports it.</summary>
internal static class GeometryTypeNames
{
    public const string Unknown = "Unknown";

    public static string Of(IGeometry? geometry) => geometry switch
    {
        IPoint or IMultiPoint => "Point",
        ILineString or IMultiLineString => "LineString",
        IPolygon or IMultiPolygon => "Polygon",
        _ => Unknown,
    };
}
