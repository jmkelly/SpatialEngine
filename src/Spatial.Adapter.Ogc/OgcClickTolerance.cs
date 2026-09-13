using System.Text.Json;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// The GetFeatureInfo click tolerance for a feature layer (ADR-0053 §3): a
/// point feature's marker paints a disk around its coordinate, so a click
/// anywhere inside that disk must identify the feature even though the query
/// geometry only covers the centre. The radius is read from the layer's
/// persisted MapLibre fragments (ADR-0047); a layer without a circle marker
/// contributes nothing and the caller adds the clicked-pixel allowance.
/// </summary>
internal static class OgcClickTolerance
{
    /// <summary>Half a pixel covers the clicked column/row around its integer coordinate.</summary>
    public const double BasePixels = 0.5;

    /// <summary>The radius in pixels of the widest circle marker in a persisted style, or 0 when there is none.</summary>
    public static double MarkerRadiusPixels(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            return 0;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(style);
        }
        catch (JsonException)
        {
            return 0;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            var radius = 0d;
            foreach (var fragment in document.RootElement.EnumerateArray())
            {
                radius = Math.Max(radius, CircleRadius(fragment));
            }

            return radius;
        }
    }

    private static double CircleRadius(JsonElement fragment)
    {
        if (fragment.ValueKind != JsonValueKind.Object
            || !fragment.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "circle", StringComparison.Ordinal)
            || !fragment.TryGetProperty("paint", out var paint)
            || paint.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        // The visible disk includes the stroke, which straddles the radius.
        return Number(paint, "circle-radius") + (Number(paint, "circle-stroke-width") / 2);
    }

    private static double Number(JsonElement paint, string name) =>
        paint.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? Math.Max(number, 0)
            : 0;
}
