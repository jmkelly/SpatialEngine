using System.Text.Json;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The export temporal parameters (S2 export-map/, T-040): <c>time</c>,
/// <c>timeRelation</c> and <c>layerTimeOptions</c>. <c>time</c> reuses the
/// query grammar (<see cref="EsriFeatureQuery.ParseTime"/>); the resolved
/// per-layer <see cref="MapTimeExtent"/> values ride the render contract so
/// every store filters the same way (the demo and memory stores reject or
/// ignore store-level filters, so a where-clause translation would not
/// hold there). <c>timeRelation</c> reads the three relations the
/// temporal-extent model can serve and refuses the rest by name; the two that
/// need an extent are additionally refused on a layer that designates none
/// (ADR-0175 §6, ADR-0182).
/// </summary>
internal static class MapExportTime
{
    /// <summary>The default temporal relation: the queried extent overlaps the feature's extent.</summary>
    public const string Overlaps = "esriTimeRelationOverlaps";

    /// <summary>The feature's extent covers the requested window.</summary>
    public const string Contains = "esriTimeRelationContains";

    /// <summary>The feature's extent is covered by the requested window.</summary>
    public const string Within = "esriTimeRelationWithin";

    /// <summary>
    /// The temporal relations the engine serves (S2): the three a feature
    /// temporal extent evaluates (ADR-0175 §3). Every other value is a typed
    /// <c>invalid.arguments</c> rather than a silently widened temporal query
    /// (ADR-0100 — the same rule as a non-zero <c>timeOffset</c> below).
    /// </summary>
    private static readonly (string Name, TemporalRelation Relation)[] Relations =
    [
        (Overlaps, TemporalRelation.Overlaps),
        (Contains, TemporalRelation.Contains),
        (Within, TemporalRelation.Within),
    ];

    /// <summary>
    /// Parses the export <c>timeRelation</c>: blank means the default
    /// (<c>esriTimeRelationOverlaps</c>); anything the engine does not serve
    /// is a typed <c>invalid.arguments</c> naming the three it does, rather
    /// than a silently widened temporal query (ADR-0100).
    /// </summary>
    public static TemporalRelation? ParseTimeRelation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        foreach (var (name, relation) in Relations)
        {
            if (string.Equals(name, value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return relation;
            }
        }

        throw GeoServicesErrors.Invalid(
            $"The 'timeRelation' value '{value}' is not supported: the engine serves the overlaps, contains and within relations over a feature's designated temporal extent, so only '{Overlaps}', '{Contains}' and '{Within}' are applied, and the root's supportsTimeRelation says whether a layer can answer all three.");
    }

    /// <summary>The Esri spelling of a relation, for a message that names the one refused.</summary>
    public static string Name(TemporalRelation relation) =>
        Relations.First(pair => pair.Relation == relation).Name;

    /// <summary>
    /// The typed refusal a reader raises when a relation that needs a feature
    /// temporal extent reaches a layer that designates none. The request
    /// edge refuses it first and names the layer; this is the backstop behind
    /// it, so a relation is never accepted and answered with the bag rule
    /// (ADR-0100's rule, kept).
    /// </summary>
    public static EsriInteropException Undesignated(TemporalRelation relation) =>
        GeoServicesErrors.Invalid(
            $"The temporal relation '{Name(relation)}' is not supported: the layer designates no start/end date field, so a feature has no temporal extent to compare the requested window against. Send '{Overlaps}' instead.");

    /// <summary>
    /// Refuses a relation that needs a feature temporal extent on a layer that
    /// designates none: the engine has no start and end to compare the window
    /// against, so the request is rejected by name rather than answered with
    /// the bag rule (ADR-0175 §6). <c>overlaps</c> is served on every layer —
    /// it is the relation the undesignated rule already is — so it never
    /// reaches here.
    /// </summary>
    public static void RequireDesignated(TemporalRelation relation, IReadOnlyList<MapDesignation> layers)
    {
        if (relation is TemporalRelation.Overlaps)
        {
            return;
        }

        foreach (var layer in layers)
        {
            if (layer.Fields is null or { IsEmpty: true })
            {
                throw GeoServicesErrors.Invalid(
                    $"The 'timeRelation' value '{Name(relation)}' is not supported for layer {layer.Id} ('{layer.Name}'): the layer designates no start/end date field, so a feature has no temporal extent to compare the requested window against. Send '{Overlaps}' instead.");
            }
        }
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
    /// data up to the end of the window (an open start). The relation the
    /// request named rides each extent, so the reader applies the one the
    /// client asked for rather than the default (ADR-0100's Context). Null
    /// when no <paramref name="time"/> was requested, so unfiltered renders
    /// stay byte-identical to the pre-time behaviour.
    /// </summary>
    public static IReadOnlyDictionary<int, MapTimeExtent>? ResolveTimes(
        IReadOnlyList<PublishedLayer> layers,
        EsriTimeExtent? time,
        IReadOnlyDictionary<int, MapLayerTimeOption>? options,
        TemporalRelation? relation = null)
    {
        if (time is null)
        {
            return null;
        }

        var applied = relation ?? TemporalRelation.Overlaps;
        var resolved = new Dictionary<int, MapTimeExtent>();
        foreach (var layer in layers)
        {
            var option = options?.GetValueOrDefault(layer.Id);
            if (option is { UseTime: false })
            {
                continue;
            }

            var start = option is { Cumulative: true } ? null : time.StartMs;
            resolved[layer.Id] = new MapTimeExtent(start, time.EndMs) { Relation = applied };
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

/// <summary>
/// One served layer's temporal designation, as the request edge sees it: the
/// layer a client asked about and the fields its schema designates as the
/// dates bounding a feature (ADR-0175), or <c>null</c> when it designates
/// none. What <see cref="MapExportTime.RequireDesignated"/> judges a
/// requested relation against.
/// </summary>
/// <param name="Id">The layer's served id.</param>
/// <param name="Name">The layer's served name, for a message the client can act on.</param>
/// <param name="Fields">The designated start/end date fields, or <c>null</c>.</param>
internal sealed record MapDesignation(int Id, string Name, TemporalExtentFields? Fields);
