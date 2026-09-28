using System.Globalization;
using System.Text.Json;
using Spatial.Contracts;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>
/// Reads the MapLibre expression subset from JSON. An unsupported operator, a
/// malformed operand list or a wrong operand type is a typed
/// <c>invalid.arguments</c> naming the offending path, so a style the
/// renderer cannot honour is rejected with an actionable message instead of
/// being flattened.
/// </summary>
internal static class ExpressionReader
{
    /// <summary>Reads one expression, interning it against the style being compiled.</summary>
    public static StyleExpression Read(JsonElement element, ExpressionInterner? interner = null) =>
        interner is null ? Read(element) : interner.Intern(element, Read);

    /// <summary>
    /// Reads one expression: an array is an operator form, anything else a
    /// literal. The whole compiled tree is then validated once, so a nested
    /// operand is never judged without the <c>let</c> bindings around it.
    /// </summary>
    public static StyleExpression Read(JsonElement element)
    {
        var expression = ExpressionReader.Compile(element);
        ExpressionValidator.Validate(expression);
        return expression;
    }

    /// <summary>Compiles one expression without the whole-tree validation pass.</summary>
    internal static StyleExpression Compile(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array
            ? Operators.Read(element)
            : new LiteralExpression(Literals.Read(element, "expression"));
}

/// <summary>
/// Shares one compiled node between every place in a style document that
/// writes the same expression text. The per-feature memo is keyed on the node,
/// so interning is what makes a repeated expression — the same radius in two
/// layers, say — evaluate once per feature instead of once per property per
/// layer.
/// </summary>
internal sealed class ExpressionInterner
{
    private readonly Dictionary<string, StyleExpression> _shared = new(StringComparer.Ordinal);

    /// <summary>How many reads were served from the table (the measurement the cache tests assert on).</summary>
    public int Hits { get; private set; }

    public StyleExpression Intern(JsonElement element, Func<JsonElement, StyleExpression> read)
    {
        var key = element.GetRawText();
        if (_shared.TryGetValue(key, out var existing))
        {
            Hits++;
            return existing;
        }

        var compiled = read(element);
        _shared[key] = compiled;
        return compiled;
    }
}

/// <summary>Literal JSON values: numbers, strings, booleans, null and CSS colours.</summary>
internal static class Literals
{
    public static ExpressionValue Read(JsonElement element, string what) => element.ValueKind switch
    {
        JsonValueKind.Null => ExpressionValue.Missing,
        JsonValueKind.True => ExpressionValue.OfFlag(true),
        JsonValueKind.False => ExpressionValue.OfFlag(false),
        JsonValueKind.Number => ExpressionValue.OfNumber(element.GetDouble()),
        JsonValueKind.String => Text(element.GetString(), what),
        _ => throw SpatialException.BadArguments($"Expression {what} must be a number, string, boolean or null."),
    };

    /// <summary>A CSS colour string, or the string itself when it is not a colour.</summary>
    public static ExpressionValue Text(string? value, string what)
    {
        _ = what;
        return value is null
            ? ExpressionValue.Missing
            : StyleColorParser.TryParse(value, out var color) && IsSyntacticColour(value)
                ? ExpressionValue.OfColor(color)
                : ExpressionValue.OfText(value);
    }

    /// <summary>
    /// Only the syntactic colour forms (<c>#rgb…</c>, <c>rgb(…)</c>,
    /// <c>transparent</c>) type as a colour: a bare name is both a MapLibre
    /// colour and an ordinary string, and a label must stay a string.
    /// </summary>
    private static bool IsSyntacticColour(string value) =>
        value.StartsWith('#')
        || value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)
        || value.Equals("transparent", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The operator forms: dispatch on the leading operator name.</summary>
internal static class Operators
{
    public static StyleExpression Read(JsonElement element)
    {
        if (element.GetArrayLength() == 0 || element[0].ValueKind != JsonValueKind.String)
        {
            throw SpatialException.BadArguments(
                "An expression must be a value or a non-empty array of an operator and its operands.");
        }

        var name = element[0].GetString()!;
        var operands = element.EnumerateArray().Skip(1).ToArray();
        return name switch
        {
            "literal" => Literal(element, operands),
            "get" => Attribute(element, operands),
            "has" => Presence(element, operands),
            "var" => Variable(element, operands),
            "let" => Let(element, operands),
            "zoom" => Nullary(element, "zoom", operands, new ZoomExpression()),
            "id" => Nullary(element, "id", operands, new IdentityExpression()),
            "geometry-type" => Nullary(element, "geometry-type", operands, new GeometryTypeExpression()),
            "==" or "!=" or "<" or "<=" or ">" or ">=" => Comparison(name, element, operands),
            "all" or "any" => Logical(name, operands),
            "!" => new NegationExpression(Unary(element, operands)),
            "in" => Membership(element, operands),
            "+" or "-" or "*" or "/" or "%" => Arithmetic(name, element, operands),
            "concat" => new ConcatExpression(Operands(element, operands, "concat")),
            "case" => Case(element, operands),
            "match" => Match(element, operands),
            "coalesce" => new CoalesceExpression(Operands(element, operands, "coalesce")),
            "step" => Step(element, operands),
            "interpolate" => Interpolate(element, operands),
            "at-interpolate" => AtInterpolate(element, operands),
            "to-color" => ToColor(element, operands),
            _ => throw SpatialException.BadArguments(
                $"Unsupported expression operator '{name}' in {element.GetRawText()}."),
        };
    }

    private static LiteralExpression Literal(JsonElement element, JsonElement[] operands)
    {
        RequireExactly(element, "literal", operands, 1);
        return new LiteralExpression(Literals.Read(operands[0], "'literal'"));
    }

    private static AttributeExpression Attribute(JsonElement element, JsonElement[] operands)
    {
        if (operands.Length is not (1 or 2))
        {
            throw WrongOperands(element, "get", "1 or 2");
        }

        var fallback = operands.Length == 2 ? Literals.Read(operands[1], "'get'") : ExpressionValue.Missing;
        return new AttributeExpression(Name(operands[0], element, "get"), fallback);
    }

    private static PresenceExpression Presence(JsonElement element, JsonElement[] operands)
    {
        RequireExactly(element, "has", operands, 1);
        return new PresenceExpression(Name(operands[0], element, "has"));
    }

    private static VariableExpression Variable(JsonElement element, JsonElement[] operands)
    {
        RequireExactly(element, "var", operands, 1);
        return new VariableExpression(Name(operands[0], element, "var"));
    }

    private static LetExpression Let(JsonElement element, JsonElement[] operands)
    {
        if (operands.Length < 3 || operands.Length % 2 == 0)
        {
            throw WrongOperands(element, "let", "an odd count of at least 3");
        }

        var bindings = new List<KeyValuePair<string, StyleExpression>>();
        for (var index = 0; index < operands.Length - 1; index += 2)
        {
            bindings.Add(new KeyValuePair<string, StyleExpression>(
                Name(operands[index], element, "let"), ExpressionReader.Compile(operands[index + 1])));
        }

        return new LetExpression(bindings, ExpressionReader.Compile(operands[^1]));
    }

    private static StyleExpression Nullary(JsonElement element, string name, JsonElement[] operands, StyleExpression expression)
    {
        RequireExactly(element, name, operands, 0);
        return expression;
    }

    private static ComparisonExpression Comparison(string name, JsonElement element, JsonElement[] operands)
    {
        RequireExactly(element, name, operands, 2);
        return new ComparisonExpression(name, ExpressionReader.Compile(operands[0]), ExpressionReader.Compile(operands[1]));
    }

    private static LogicalExpression Logical(string name, JsonElement[] operands) =>
        new(name, operands.Select(ExpressionReader.Read).ToArray());

    private static MembershipExpression Membership(JsonElement element, JsonElement[] operands)
    {
        if (operands.Length < 2)
        {
            throw WrongOperands(element, "in", "at least 2");
        }

        // The haystack is a string (or an expression yielding one) and is
        // tested by containment; the trailing operands are the spec's "one of"
        // literals and are compared for equality.
        var extras = new List<ExpressionValue>();
        for (var index = 2; index < operands.Length; index++)
        {
            extras.Add(Literals.Read(operands[index], "'in'"));
        }

        return new MembershipExpression(
            ExpressionReader.Compile(operands[0]), ExpressionReader.Compile(operands[1]), extras);
    }

    private static CaseExpression Case(JsonElement element, JsonElement[] operands)
    {
        if (operands.Length < 3 || operands.Length % 2 == 0)
        {
            throw WrongOperands(element, "case", "an odd count of at least 3");
        }

        var branches = new List<KeyValuePair<StyleExpression, StyleExpression>>();
        for (var index = 0; index < operands.Length - 1; index += 2)
        {
            branches.Add(new KeyValuePair<StyleExpression, StyleExpression>(
                ExpressionReader.Compile(operands[index]), ExpressionReader.Compile(operands[index + 1])));
        }

        return new CaseExpression(branches, ExpressionReader.Compile(operands[^1]));
    }

    private static MatchExpression Match(JsonElement element, JsonElement[] operands)
    {
        if (operands.Length < 4 || operands.Length % 2 != 0)
        {
            throw WrongOperands(element, "match", "an even count of at least 4");
        }

        var arms = new List<MatchArm>();
        for (var index = 1; index < operands.Length - 1; index += 2)
        {
            arms.Add(new MatchArm(Labels(operands[index]), ExpressionReader.Compile(operands[index + 1])));
        }

        return new MatchExpression(
            ExpressionReader.Compile(operands[0]), arms, ExpressionReader.Compile(operands[^1]));
    }

    private static StepExpression Step(JsonElement element, JsonElement[] operands)
    {
        if (operands.Length < 4 || operands.Length % 2 != 0)
        {
            throw WrongOperands(element, "step", "an even count of at least 4");
        }

        return new StepExpression(
            ExpressionReader.Compile(operands[0]), ExpressionReader.Compile(operands[1]), Stops(element, operands, 2));
    }

    private static InterpolateExpression Interpolate(JsonElement element, JsonElement[] operands)
    {
        if (operands.Length < 6 || operands.Length % 2 != 0)
        {
            throw WrongOperands(element, "interpolate", "an even count of at least 6");
        }

        return new InterpolateExpression(
            ExpressionReader.Compile(operands[1]), EasingOf(element, operands[0]), Stops(element, operands, 2));
    }

    private static AtInterpolateExpression AtInterpolate(JsonElement element, JsonElement[] operands)
    {
        // The sampled point sits between the input and the stop list, so the
        // operand count past it is even: an input, a stop and at least two
        // stops, as interpolate itself requires.
        if (operands.Length < 7 || operands.Length % 2 == 0)
        {
            throw WrongOperands(element, "at-interpolate", "an odd count of at least 7");
        }

        if (operands[2].ValueKind != JsonValueKind.Number)
        {
            throw SpatialException.BadArguments(
                $"The 'at-interpolate' stop in {element.GetRawText()} must be a number, got {operands[2].GetRawText()}.");
        }

        return new AtInterpolateExpression(
            ExpressionReader.Compile(operands[1]),
            operands[2].GetDouble(),
            EasingOf(element, operands[0]),
            Stops(element, operands, 3));
    }

    private static ToColorExpression ToColor(JsonElement element, JsonElement[] operands)
    {
        RequireExactly(element, "to-color", operands, 1);
        var operand = ExpressionReader.Compile(operands[0]);
        if (operand.Type == ExpressionType.Color)
        {
            // A colour is not a number and a number is not a colour: the one
            // coercion the dialect serves is not a laundering of both ways.
            throw SpatialException.BadArguments(
                $"'to-color' in {element.GetRawText()} reads a number or a colour name, but its operand is already a colour.");
        }

        return new ToColorExpression(operand);
    }

    private static Easing EasingOf(JsonElement element, JsonElement argument)
    {
        if (argument.ValueKind != JsonValueKind.Array || argument.GetArrayLength() == 0)
        {
            throw SpatialException.BadArguments(
                $"'interpolate' in {element.GetRawText()} needs an interpolation, got {argument.GetRawText()}.");
        }

        var name = argument[0].ValueKind == JsonValueKind.String ? argument[0].GetString() : null;
        return name switch
        {
            "linear" => Easing.Linear,
            "exponential" => Easing.Exponential(ExponentialBase(element, argument)),
            "cubic-bezier" => Easing.Bezier(CubicBezier(element, argument)),
            _ => throw SpatialException.BadArguments(
                $"Unsupported interpolation in {argument.GetRawText()}; only 'linear', 'exponential' and 'cubic-bezier' are served."),
        };
    }

    /// <summary>
    /// The CSS easing curve <c>["cubic-bezier", x1, y1, x2, y2]</c>. Trailing
    /// values may be omitted and default to zero, so <c>["cubic-bezier"]</c> is
    /// the identity curve and a half-written curve is still a curve — but the
    /// two abscissas must lie in [0, 1] or the curve is not monotonic in the
    /// input and the ramp would not be a function of it.
    /// </summary>
    private static CubicBezier CubicBezier(JsonElement element, JsonElement argument)
    {
        if (argument.GetArrayLength() > 5)
        {
            throw SpatialException.BadArguments(
                $"The 'cubic-bezier' in {element.GetRawText()} takes at most four numbers, got {argument.GetRawText()}.");
        }

        var values = new double[4];
        for (var index = 1; index < argument.GetArrayLength(); index++)
        {
            if (argument[index].ValueKind != JsonValueKind.Number)
            {
                throw SpatialException.BadArguments(
                    $"A 'cubic-bezier' control point in {element.GetRawText()} must be a number, got {argument[index].GetRawText()}.");
            }

            values[index - 1] = argument[index].GetDouble();
        }

        if (values[0] is < 0 or > 1 || values[2] is < 0 or > 1)
        {
            throw SpatialException.BadArguments(
                $"The 'cubic-bezier' abscissas x1 and x2 in {element.GetRawText()} must lie in [0, 1], got {Number(values[0])} and {Number(values[2])}.");
        }

        return new CubicBezier(values[0], values[1], values[2], values[3]);
    }

    private static double ExponentialBase(JsonElement element, JsonElement argument)
    {
        if (argument.GetArrayLength() == 1)
        {
            return 1;
        }

        if (argument[1].ValueKind != JsonValueKind.Number)
        {
            throw SpatialException.BadArguments(
                $"The 'exponential' base in {element.GetRawText()} must be a number, got {argument[1].GetRawText()}.");
        }

        return argument[1].GetDouble();
    }

    private static List<KeyValuePair<double, StyleExpression>> Stops(
        JsonElement element, JsonElement[] operands, int from)
    {
        var stops = new List<KeyValuePair<double, StyleExpression>>();
        for (var index = from; index < operands.Length; index += 2)
        {
            if (operands[index].ValueKind != JsonValueKind.Number)
            {
                throw SpatialException.BadArguments(
                    $"A stop in {element.GetRawText()} must be a number, got {operands[index].GetRawText()}.");
            }

            var stop = operands[index].GetDouble();
            if (stops.Count > 0 && stop <= stops[^1].Key)
            {
                throw SpatialException.BadArguments(
                    $"Stops in {element.GetRawText()} must ascend; {Number(stop)} follows {Number(stops[^1].Key)}.");
            }

            stops.Add(new KeyValuePair<double, StyleExpression>(stop, ExpressionReader.Compile(operands[index + 1])));
        }

        return stops;
    }

    private static ExpressionValue[] Labels(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Select(item => Literals.Read(item, "'match'")).ToArray();
        }

        return [Literals.Read(element, "'match'")];
    }

    private static StyleExpression[] Operands(JsonElement element, JsonElement[] operands, string name)
    {
        if (operands.Length == 0)
        {
            throw WrongOperands(element, name, "at least 1");
        }

        return operands.Select(ExpressionReader.Read).ToArray();
    }

    private static ArithmeticExpression Arithmetic(string name, JsonElement element, JsonElement[] operands)
    {
        // Only minus has a unary form; the rest are binary arithmetic.
        if (operands.Length != 2 && !(operands.Length == 1 && name == "-"))
        {
            throw WrongOperands(element, name, "2, or 1 for unary '-");
        }

        var left = ExpressionReader.Compile(operands[0]);
        return new ArithmeticExpression(name, left, operands.Length == 2 ? ExpressionReader.Compile(operands[1]) : null);
    }

    private static StyleExpression Unary(JsonElement element, JsonElement[] operands)
    {
        RequireExactly(element, "!", operands, 1);
        return ExpressionReader.Compile(operands[0]);
    }

    private static void RequireExactly(JsonElement element, string name, JsonElement[] operands, int expected)
    {
        if (operands.Length != expected)
        {
            throw WrongOperands(element, name, $"exactly {expected}");
        }
    }

    private static string Name(JsonElement element, JsonElement owner, string what) =>
        element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw SpatialException.BadArguments(
                $"The '{what}' name in {owner.GetRawText()} must be a string, got {element.GetRawText()}.");

    private static SpatialException WrongOperands(JsonElement element, string name, string rule) =>
        SpatialException.BadArguments(
            $"Expression '{name}' in {element.GetRawText()} needs {rule} operands.");

    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
