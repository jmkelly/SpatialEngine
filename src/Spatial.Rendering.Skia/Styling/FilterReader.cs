using System.Text.Json;
using Spatial.Contracts;
using Spatial.Core.Features;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>
/// Reads the MapLibre <c>filter</c> expression subset into a
/// <see cref="StyleFilter"/>. The supported operators are <c>==</c>,
/// <c>!=</c>, <c>has</c>, <c>!has</c>, <c>in</c>, <c>all</c>, <c>any</c>,
/// <c>none</c> and <c>!</c>; anything else is a typed
/// <c>invalid.arguments</c> naming the expression.
/// </summary>
internal static class FilterReader
{
    public static StyleFilter Read(JsonElement element)
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
        return new NotFilter(Read(element[1]));
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
            children.Add(Read(element[i]));
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
