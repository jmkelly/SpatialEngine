using System.Text.Json;
using Spatial.Contracts;
using Spatial.Core.Features;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>
/// Reads a MapLibre <c>filter</c>. The legacy operator grammar (<c>==</c>,
/// <c>!=</c>, <c>has</c>, <c>!has</c>, <c>in</c>, <c>all</c>, <c>any</c>,
/// <c>none</c>, <c>!</c> over literal operands) lowers onto the compiled
/// <see cref="StyleFilter"/> model; anything else is read as an
/// <see cref="ExpressionFilter"/> in the expression dialect, which must yield
/// a boolean. Both are typed <c>invalid.arguments</c> failures.
/// </summary>
internal static class FilterReader
{
    /// <summary>The legacy operators; an array of one of these over literal operands stays legacy.</summary>
    private static readonly HashSet<string> LegacyOperators = new(StringComparer.Ordinal)
    {
        "==", "!=", "has", "!has", "in", "all", "any", "none", "!",
    };

    public static StyleFilter Read(JsonElement element, ExpressionInterner? interner = null) =>
        IsLegacy(element) ? ReadLegacy(element) : new ExpressionFilter(ReadExpression(element, interner));

    /// <summary>
    /// Whether the whole filter is the legacy grammar: a legacy operator whose
    /// entire subtree is literals. One nested array anywhere (a
    /// <c>["get", …]</c> operand, say) makes the whole filter an expression, so
    /// the two dialects never mix halfway.
    /// </summary>
    private static bool IsLegacy(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0
            || element[0].ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return IsLegacyBody(element[0].GetString()!, element);
    }

    /// <summary>
    /// A nesting operator (<c>all</c>, <c>any</c>, <c>none</c>, <c>!</c>)
    /// takes legacy children; every other legacy operator takes literals, so a
    /// nested array under it means the writer used the expression dialect.
    /// </summary>
    private static bool IsLegacyBody(string op, JsonElement element)
    {
        var nested = op is "all" or "any" or "none" or "!";
        foreach (var operand in element.EnumerateArray().Skip(1))
        {
            if (operand.ValueKind == JsonValueKind.Object)
            {
                return false;
            }

            if (operand.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            if (!nested || !IsLegacy(operand))
            {
                return false;
            }
        }

        return true;
    }

    private static StyleExpression ReadExpression(JsonElement element, ExpressionInterner? interner)
    {
        var expression = ExpressionReader.Read(element, interner);
        if (expression.Type is not (ExpressionType.Flag or ExpressionType.Any))
        {
            throw SpatialException.BadArguments(
                $"A filter must yield a boolean, but {expression} yields {expression.Type.ToString().ToLowerInvariant()}.");
        }

        return expression;
    }

    private static StyleFilter ReadLegacy(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
        {
            throw SpatialException.BadArguments("A filter must be a non-empty JSON array.");
        }

        var op = ReadString(element[0], "filter operator");
        return op switch
        {
            "==" or "!=" => Comparison(op, element),
            "has" or "!has" => Presence(op, element),
            "in" => ReadIn(element),
            "!" => Unary(element),
            "all" or "any" or "none" => Logical(op, element),
            _ => throw SpatialException.BadArguments($"Unsupported filter expression '{op}'."),
        };
    }

    private static StyleFilter Comparison(string op, JsonElement element)
    {
        RequireLength(element, 3, op);
        var field = ReadString(element[1], op);
        var value = ReadValue(element[2]);
        return op == "==" ? new EqualsFilter(field, value) : new NotEqualsFilter(field, value);
    }

    private static HasFilter Presence(string op, JsonElement element)
    {
        RequireLength(element, 2, op);
        return new HasFilter(ReadString(element[1], op), Negated: op == "!has");
    }

    private static StyleFilter Logical(string op, JsonElement element)
    {
        var children = ReadChildren(element);
        return op switch
        {
            "all" => new AllFilter(children),
            "any" => new AnyFilter(children),
            _ => new NotFilter(new AnyFilter(children)),
        };
    }

    private static NotFilter Unary(JsonElement element)
    {
        RequireLength(element, 2, "!");
        return new NotFilter(ReadLegacy(element[1]));
    }

    private static InFilter ReadIn(JsonElement element)
    {
        RequireLength(element, 3, "in");
        var field = ReadString(element[1], "in");
        var values = new List<AttributeValue>(element.GetArrayLength() - 2);
        for (var i = 2; i < element.GetArrayLength(); i++)
        {
            values.Add(ReadValue(element[i]));
        }

        return new InFilter(field, values);
    }

    private static List<StyleFilter> ReadChildren(JsonElement element)
    {
        var children = new List<StyleFilter>(element.GetArrayLength() - 1);
        for (var i = 1; i < element.GetArrayLength(); i++)
        {
            children.Add(ReadLegacy(element[i]));
        }

        return children;
    }

    private static void RequireLength(JsonElement element, int minimum, string op)
    {
        if (element.GetArrayLength() < minimum)
        {
            throw SpatialException.BadArguments($"Filter expression '{op}' requires at least {minimum} elements.");
        }
    }

    private static string ReadString(JsonElement element, string what)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw SpatialException.BadArguments($"Filter {what} must be a string.");
        }

        return element.GetString() ?? string.Empty;
    }

    private static AttributeValue ReadValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => AttributeValue.FromString(element.GetString() ?? string.Empty),
        JsonValueKind.True => AttributeValue.FromBoolean(true),
        JsonValueKind.False => AttributeValue.FromBoolean(false),
        JsonValueKind.Number => ReadNumber(element),
        _ => throw SpatialException.BadArguments("Filter values must be strings, numbers or booleans."),
    };

    private static AttributeValue ReadNumber(JsonElement element) =>
        element.TryGetInt64(out var integer)
            ? AttributeValue.FromInt64(integer)
            : AttributeValue.FromDouble(element.GetDouble());
}
