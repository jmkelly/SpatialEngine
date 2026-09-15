using System.Text.Json;
using Spatial.Contracts;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The export temporal parameters (S2 export-map/, T-040): <c>time</c>,
/// <c>timeRelation</c> and <c>layerTimeOptions</c>. <c>time</c> reuses the
/// query grammar (<see cref="EsriFeatureQuery.ParseTime"/>); the resolved
/// per-layer <see cref="MapTimeExtent"/> values ride the render contract so
/// every store filters the same way (the demo and memory stores reject or
/// ignore store-level filters, so a where-clause translation would not
/// hold there).
/// </summary>
internal static class MapExportTime
{
    /// <summary>The default temporal relation: the queried extent overlaps the feature's instant.</summary>
    public const string Overlaps = "esriTimeRelationOverlaps";

    /// <summary>The documented temporal relations (S2). The engine's date values are instants, so all three reduce to containment.</summary>
    private static readonly string[] Relations =
    [
        Overlaps,
        "esriTimeRelationContains",
        "esriTimeRelationWithin",
    ];

    /// <summary>
    /// Parses the export <c>timeRelation</c>: blank means the default
    /// (<c>esriTimeRelationOverlaps</c>); anything outside the documented set
    /// is a typed <c>invalid.arguments</c> rather than a silently widened
    /// temporal query.
    /// </summary>
    public static string? ParseTimeRelation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var relation = Relations.FirstOrDefault(candidate =>
            string.Equals(candidate, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return relation
            ?? throw GeoServicesErrors.Invalid(
                $"The 'timeRelation' value '{value}' is not supported (esriTimeRelationOverlaps, esriTimeRelationContains, esriTimeRelationWithin).");
    }

    /// <summary>
    /// Parses the export <c>layerTimeOptions</c> JSON array
    /// (<c>[{"id":0,"useTime":false,"timeDataCumulative":true}]</c>) into the
    /// per-layer temporal behaviour. The id accepts a number or a numeric
    /// string; <c>useTime</c> defaults to true. A zero <c>timeOffset</c> is a
    /// no-op; a non-zero offset has no engine model behind it and is a typed
    /// <c>invalid.arguments</c> rather than a silently ignored shift.
    /// </summary>
    public static IReadOnlyDictionary<int, MapLayerTimeOption>? ParseLayerTimeOptions(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        using var document = ParseDocument(value);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw GeoServicesErrors.Invalid("'layerTimeOptions' must be a JSON array of per-layer time options.");
        }

        var options = new Dictionary<int, MapLayerTimeOption>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw GeoServicesErrors.Invalid("'layerTimeOptions' entries must be objects with an integer 'id'.");
            }

            var id = OptionId(element);
            var useTime = OptionBool(element, "useTime", true);
            var cumulative = OptionBool(element, "timeDataCumulative", false);
            OptionOffset(element);
            options[id] = new MapLayerTimeOption(useTime, cumulative);
        }

        return options.Count == 0 ? null : options;
    }

    /// <summary>
    /// Resolves the render temporal extent per selected layer: layers opted
    /// out by <c>useTime:false</c> carry none; cumulative layers show all
    /// data up to the end of the window (an open start). Null when no
    /// <paramref name="time"/> was requested, so unfiltered renders stay
    /// byte-identical to the pre-time behaviour.
    /// </summary>
    public static IReadOnlyDictionary<int, MapTimeExtent>? ResolveTimes(
        IReadOnlyList<PublishedLayer> layers, EsriTimeExtent? time, IReadOnlyDictionary<int, MapLayerTimeOption>? options)
    {
        if (time is null)
        {
            return null;
        }

        var resolved = new Dictionary<int, MapTimeExtent>();
        foreach (var layer in layers)
        {
            var option = options?.GetValueOrDefault(layer.Id);
            if (option is { UseTime: false })
            {
                continue;
            }

            var start = option is { Cumulative: true } ? null : time.StartMs;
            resolved[layer.Id] = new MapTimeExtent(start, time.EndMs);
        }

        return resolved;
    }

    private static JsonDocument ParseDocument(string value)
    {
        try
        {
            return JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            throw GeoServicesErrors.Invalid("'layerTimeOptions' must be a JSON array of per-layer time options.");
        }
    }

    private static int OptionId(JsonElement element)
    {
        if (element.TryGetProperty("id", out var id))
        {
            if (id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var number))
            {
                return number;
            }

            if (id.ValueKind == JsonValueKind.String
                && int.TryParse(id.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        throw GeoServicesErrors.Invalid("'layerTimeOptions' entries must carry an integer 'id'.");
    }

    private static bool OptionBool(JsonElement element, string name, bool fallback)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return fallback;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw GeoServicesErrors.Invalid($"'layerTimeOptions' entry '{name}' must be a boolean."),
        };
    }

    private static void OptionOffset(JsonElement element)
    {
        if (!element.TryGetProperty("timeOffset", out var offset)
            || offset.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        var shift = offset.ValueKind == JsonValueKind.Number ? offset.GetDouble() : double.NaN;
        if (!double.IsFinite(shift))
        {
            throw GeoServicesErrors.Invalid("'layerTimeOptions' entry 'timeOffset' must be a number.");
        }

        if (shift != 0)
        {
            throw GeoServicesErrors.Invalid(
                "The 'layerTimeOptions' entry 'timeOffset' is not supported: the engine has no per-layer time-shift model, so a shifted layer cannot be rendered honestly.");
        }
    }
}

/// <summary>One layer's export temporal behaviour: whether it honours <c>time</c>, and whether it shows cumulative data.</summary>
internal sealed record MapLayerTimeOption(bool UseTime, bool Cumulative);
