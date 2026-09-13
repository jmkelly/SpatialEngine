using System.Globalization;
using System.Text.Json;
using Spatial.PluginSdk;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>
/// Wraps one style sub-object (<c>paint</c> or <c>layout</c>) and tracks which
/// properties were consumed. Reading an unsupported value throws a typed
/// <c>invalid.arguments</c>; <see cref="RejectUnsupported"/> throws for any
/// property that was never read, so the documented subset is never silently
/// flattened.
/// </summary>
internal sealed class StylePropertyBag
{
    private readonly JsonElement _element;
    private readonly string _label;
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);

    public StylePropertyBag(JsonElement element, string label)
    {
        _element = element;
        _label = label;
    }

    public StyleColor Color(string name, StyleColor fallback)
    {
        if (!TryGet(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.String || !StyleColorParser.TryParse(value.GetString(), out var color))
        {
            throw SpatialException.BadArguments($"Unsupported value for '{name}': expected a CSS colour.");
        }

        return color;
    }

    public double Number(string name, double fallback)
    {
        if (!TryGet(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
        {
            throw SpatialException.BadArguments($"Unsupported value for '{name}': expected a number.");
        }

        return number;
    }

    public bool Bool(string name, bool fallback)
    {
        if (!TryGet(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw SpatialException.BadArguments($"Unsupported value for '{name}': expected a boolean."),
        };
    }

    public string? String(string name, string? fallback)
    {
        if (!TryGet(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw SpatialException.BadArguments($"Unsupported value for '{name}': expected a string.");
        }

        return value.GetString();
    }

    public List<double> NumberList(string name)
    {
        var result = new List<double>();
        if (!TryGet(name, out var value))
        {
            return result;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw SpatialException.BadArguments($"Unsupported value for '{name}': expected an array of numbers.");
        }

        foreach (var item in value.EnumerateArray())
        {
            result.Add(ReadNumber(item, name));
        }

        return result;
    }

    public IReadOnlyList<string> StringList(string name)
    {
        if (!TryGet(name, out var value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw SpatialException.BadArguments($"Unsupported value for '{name}': expected an array of strings.");
        }

        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw SpatialException.BadArguments($"Unsupported value for '{name}': expected an array of strings.");
            }

            result.Add(item.GetString()!);
        }

        return result;
    }

    public (double X, double Y) NumberPair(string name, (double X, double Y) fallback)
    {
        if (!TryGet(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
        {
            throw SpatialException.BadArguments($"Unsupported value for '{name}': expected a pair of numbers.");
        }

        var x = ReadNumber(value[0], name);
        var y = ReadNumber(value[1], name);
        return (x, y);
    }

    public LineCapStyle LineCap(string name, LineCapStyle fallback)
    {
        var text = String(name, null);
        if (text is null)
        {
            return fallback;
        }

        return text switch
        {
            "butt" => LineCapStyle.Butt,
            "round" => LineCapStyle.Round,
            "square" => LineCapStyle.Square,
            _ => throw SpatialException.BadArguments($"Unsupported value for '{name}': '{text}'."),
        };
    }

    public LineJoinStyle LineJoin(string name, LineJoinStyle fallback)
    {
        var text = String(name, null);
        if (text is null)
        {
            return fallback;
        }

        return text switch
        {
            "miter" => LineJoinStyle.Miter,
            "round" => LineJoinStyle.Round,
            "bevel" => LineJoinStyle.Bevel,
            _ => throw SpatialException.BadArguments($"Unsupported value for '{name}': '{text}'."),
        };
    }

    public void RejectUnsupported()
    {
        if (_element.ValueKind == JsonValueKind.Undefined)
        {
            return;
        }

        if (_element.ValueKind != JsonValueKind.Object)
        {
            throw SpatialException.BadArguments($"'{_label}' must be a JSON object.");
        }

        foreach (var property in _element.EnumerateObject())
        {
            if (!_consumed.Contains(property.Name))
            {
                throw SpatialException.BadArguments($"Unsupported {_label} property '{property.Name}'.");
            }
        }
    }

    private static double ReadNumber(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number))
        {
            throw SpatialException.BadArguments($"Unsupported value for '{name}': expected a number.");
        }

        return number;
    }

    private bool TryGet(string name, out JsonElement value)
    {
        if (_element.ValueKind == JsonValueKind.Object && _element.TryGetProperty(name, out value))
        {
            _consumed.Add(name);
            return true;
        }

        value = default;
        return false;
    }
}
