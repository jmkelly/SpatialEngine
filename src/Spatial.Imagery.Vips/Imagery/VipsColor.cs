using System.Globalization;
using Spatial.PluginSdk;

namespace Spatial.Imagery.Vips.Imagery;

/// <summary>A straight RGBA colour used for composite backgrounds.</summary>
internal readonly record struct VipsColor(byte Red, byte Green, byte Blue, byte Alpha = 255);

/// <summary>Parses the CSS colour subset the renderer accepts for a background.</summary>
internal static class VipsColorParser
{
    private static readonly Dictionary<string, VipsColor> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = new(255, 255, 255),
        ["black"] = new(0, 0, 0),
        ["red"] = new(255, 0, 0),
        ["green"] = new(0, 128, 0),
        ["blue"] = new(0, 0, 255),
        ["gray"] = new(128, 128, 128),
        ["grey"] = new(128, 128, 128),
        ["transparent"] = new(0, 0, 0, 0),
    };

    public static VipsColor Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !TryParse(raw, out var color))
        {
            throw SpatialException.BadArguments($"Unsupported background colour '{raw}'.");
        }

        return color;
    }

    private static readonly HashSet<int> HexLengths = [3, 6, 8];

    public static bool TryParse(string raw, out VipsColor color)
    {
        color = new VipsColor(0, 0, 0, 0);
        var text = raw.Trim();
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

    private static bool TryParseHex(string hex, out VipsColor color)
    {
        color = default;
        if (!HexLengths.Contains(hex.Length)
            || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        color = hex.Length switch
        {
            3 => new VipsColor(Nibble(value, 8), Nibble(value, 4), Nibble(value, 0)),
            6 => new VipsColor((byte)(value >> 16), (byte)(value >> 8), (byte)value),
            _ => new VipsColor((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value),
        };
        return true;
    }

    private static byte Nibble(uint value, int shift) => (byte)(((value >> shift) & 0xF) * 17);

    private static bool TryParseFunctional(string text, out VipsColor color)
    {
        color = default;
        var open = text.IndexOf('(');
        var close = text.LastIndexOf(')');
        if (open < 0 || close < open)
        {
            return false;
        }

        return TryParseChannels(text[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries), out color);
    }

    private static bool TryParseChannels(string[] parts, out VipsColor color)
    {
        color = default;
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

        color = new VipsColor(channels[0], channels[1], channels[2], alpha);
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
