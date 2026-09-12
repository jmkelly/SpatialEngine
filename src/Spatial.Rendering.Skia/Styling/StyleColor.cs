using System.Globalization;
using Spatial.PluginSdk;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>
/// A straight (non-premultiplied) RGBA colour in the compiled style model.
/// Kept free of Skia types so the style compiler is testable without touching
/// the native rasterizer surface.
/// </summary>
internal readonly record struct StyleColor(byte Red, byte Green, byte Blue, byte Alpha = 255)
{
    public static readonly StyleColor Transparent = new(0, 0, 0, 0);

    /// <summary>Multiplies the alpha channel by <paramref name="opacity"/> (clamped to [0, 1]).</summary>
    public StyleColor ScaleAlpha(double opacity)
    {
        var alpha = Math.Clamp(Alpha * opacity, 0, 255);
        return this with { Alpha = (byte)Math.Round(alpha, MidpointRounding.AwayFromZero) };
    }
}

/// <summary>
/// Parses the CSS colour syntaxes the MapLibre style spec allows in the
/// documented subset: <c>#rgb</c>, <c>#rgba</c>, <c>#rrggbb</c>,
/// <c>#rrggbbaa</c>, <c>rgb()</c>, <c>rgba()</c> and a small named table.
/// </summary>
internal static class StyleColorParser
{
    private static readonly Dictionary<string, StyleColor> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = new(255, 255, 255),
        ["black"] = new(0, 0, 0),
        ["red"] = new(255, 0, 0),
        ["green"] = new(0, 128, 0),
        ["blue"] = new(0, 0, 255),
        ["yellow"] = new(255, 255, 0),
        ["orange"] = new(255, 165, 0),
        ["gray"] = new(128, 128, 128),
        ["grey"] = new(128, 128, 128),
        ["lightgray"] = new(211, 211, 211),
        ["lightgrey"] = new(211, 211, 211),
        ["darkgray"] = new(169, 169, 169),
        ["darkgrey"] = new(169, 169, 169),
    };

    private static readonly HashSet<int> HexLengths = [3, 4, 6, 8];

    /// <summary>Parses a colour, or throws <c>invalid.arguments</c> for an unsupported syntax.</summary>
    public static StyleColor Parse(string raw) =>
        TryParse(raw, out var color)
            ? color
            : throw SpatialException.BadArguments($"Unsupported colour '{raw}'.");

    /// <summary>Parses a colour, or returns <c>false</c> when the syntax is unsupported.</summary>
    public static bool TryParse(string? raw, out StyleColor color)
    {
        color = StyleColor.Transparent;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        if (text.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (text.StartsWith('#'))
        {
            return TryParseHex(text[1..], out color);
        }

        if (text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseFunctional(text, out color);
        }

        return Names.TryGetValue(text, out color);
    }

    private static bool TryParseHex(string hex, out StyleColor color)
    {
        color = StyleColor.Transparent;
        if (!HexLengths.Contains(hex.Length)
            || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        color = hex.Length switch
        {
            3 => new StyleColor(Nibble(value, 8), Nibble(value, 4), Nibble(value, 0)),
            4 => new StyleColor(Nibble(value, 12), Nibble(value, 8), Nibble(value, 4), Nibble(value, 0)),
            6 => new StyleColor((byte)(value >> 16), (byte)(value >> 8), (byte)value),
            _ => new StyleColor((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value),
        };
        return true;
    }

    private static byte Nibble(uint value, int shift) => (byte)(((value >> shift) & 0xF) * 17);

    private static bool TryParseFunctional(string text, out StyleColor color)
    {
        color = StyleColor.Transparent;
        var open = text.IndexOf('(');
        var close = text.LastIndexOf(')');
        if (open < 0 || close < open)
        {
            return false;
        }

        return TryParseChannels(text[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries), out color);
    }

    private static bool TryParseChannels(string[] parts, out StyleColor color)
    {
        color = StyleColor.Transparent;
        if (parts.Length != 3 && parts.Length != 4)
        {
            return false;
        }

        Span<byte> channels = stackalloc byte[3];
        for (var i = 0; i < channels.Length; i++)
        {
            if (!TryChannel(parts[i], out channels[i]))
            {
                return false;
            }
        }

        var alpha = (byte)255;
        if (parts.Length == 4 && !TryAlpha(parts[3], out alpha))
        {
            return false;
        }

        color = new StyleColor(channels[0], channels[1], channels[2], alpha);
        return true;
    }

    private static bool TryChannel(string raw, out byte channel)
    {
        channel = 0;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        channel = (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
        return true;
    }

    private static bool TryAlpha(string raw, out byte alpha)
    {
        alpha = 255;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        alpha = (byte)Math.Clamp(Math.Round(value * 255, MidpointRounding.AwayFromZero), 0, 255);
        return true;
    }
}
