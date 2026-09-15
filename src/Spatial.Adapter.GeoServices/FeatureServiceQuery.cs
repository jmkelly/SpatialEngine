using System.Text.Json;
using Spatial.Interop.Esri;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The service-level <c>FeatureServer/query</c> (S1 query-feature-service/):
/// one query fanned out across the service's layers and tables. The shared
/// parameters parse exactly like a layer query (<see cref="EsriFeatureQuery"/>);
/// <c>layerDefs</c> narrows individual layers with per-layer definition
/// expressions (and per-layer <c>outFields</c> in the array syntax). When
/// <c>layerDefs</c> names layers, only those layers are queried; otherwise
/// every layer and table is. A definition expression and the shared
/// <c>where</c> both apply (conjoined), and a definition expression naming a
/// field the layer does not carry fails the request — the same strictness as
/// a layer query — rather than silently dropping the layer.
/// </summary>
internal static class FeatureServiceQuery
{
    /// <summary>One layer's <c>layerDefs</c> overrides: its definition expression and its output fields.</summary>
    internal sealed record LayerDef(EsriFilterClause? Where, IReadOnlyList<string>? OutFields);

    /// <summary>
    /// Parses the <c>layerDefs</c> parameter in its three S1 syntaxes: the
    /// simple <c>0:where;5:where</c> list, the JSON object
    /// (<c>{"0": "where"}</c>), and the JSON array with per-layer output
    /// fields (<c>[{"layerId": 0, "where": "...", "outFields": "..."}]</c>).
    /// An absent or blank value selects every layer.
    /// </summary>
    internal static IReadOnlyDictionary<int, LayerDef> ParseLayerDefs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new Dictionary<int, LayerDef>();
        }

        var text = raw.Trim();
        return text.StartsWith('{') || text.StartsWith('[')
            ? ParseJsonLayerDefs(text)
            : ParseSimpleLayerDefs(text);
    }

    /// <summary>
    /// Resolves one layer's effective query: the shared <c>where</c>
    /// conjoined with the layer's definition expression, and the layer's
    /// <c>outFields</c> when the array syntax names them.
    /// </summary>
    internal static EsriFeatureQuery ForLayer(EsriFeatureQuery query, LayerDef? def)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (def is null)
        {
            return query;
        }

        var where = (query.Where, def.Where) switch
        {
            (null, null) => null,
            (null, { } only) => only,
            ({ } only, null) => only,
            ({ } left, { } right) => left.And(right),
        };
        return query with { Where = where, OutFields = def.OutFields ?? query.OutFields };
    }

    /// <summary>
    /// Rejects the layer-level result shapes a service query cannot serve:
    /// extent, distinct values, statistics and unique ids are per-layer
    /// responses, so they stay on <c>.../&lt;layerId&gt;/query</c>.
    /// </summary>
    internal static void RejectLayerOnlyShapes(EsriFeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (LayerOnlyShape(query) is { } shape)
        {
            throw GeoServicesErrors.Invalid(
                $"The '{shape}' parameter is not supported on 'FeatureServer/query': query one layer ('.../<layerId>/query') for the per-layer result shape.");
        }
    }

    private static string? LayerOnlyShape(EsriFeatureQuery query)
    {
        if (query.ReturnExtentOnly)
        {
            return "returnExtentOnly";
        }

        if (query.ReturnDistinctValues)
        {
            return "returnDistinctValues";
        }

        if (query.OutStatistics is not null)
        {
            return "outStatistics";
        }

        if (query.ReturnUniqueIdsOnly)
        {
            return "returnUniqueIdsOnly";
        }

        return query.UniqueIds is not null ? "uniqueIds" : null;
    }

    private static Dictionary<int, LayerDef> ParseSimpleLayerDefs(string text)
    {
        var defs = new Dictionary<int, LayerDef>();
        foreach (var entry in text.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            ParseSimpleEntry(defs, entry);
        }

        if (defs.Count == 0)
        {
            throw GeoServicesErrors.Invalid("The 'layerDefs' parameter names no layers.");
        }

        return defs;
    }

    private static void ParseSimpleEntry(Dictionary<int, LayerDef> defs, string entry)
    {
        var separator = entry.IndexOf(':');
        var layerId = RequireSimpleLayerId(entry, separator);
        var where = RequireSimpleWhere(entry, separator, layerId);
        defs[layerId] = new LayerDef(ParseWhere(where, layerId), null);
    }

    private static int RequireSimpleLayerId(string entry, int separator)
    {
        if (separator >= 0
            && int.TryParse(entry[..separator].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var layerId)
            && layerId >= 0)
        {
            return layerId;
        }

        throw GeoServicesErrors.Invalid(
            $"The 'layerDefs' entry '{entry}' is not '<layerId>:<where clause>'; use the S1 simple syntax (for example '0:POP2000 > 1000000').");
    }

    private static string RequireSimpleWhere(string entry, int separator, int layerId)
    {
        var where = entry[(separator + 1)..].Trim();
        return string.IsNullOrEmpty(where)
            ? throw GeoServicesErrors.Invalid(
                $"The 'layerDefs' entry '{entry}' names no WHERE clause for layer {layerId}.")
            : where;
    }

    private static Dictionary<int, LayerDef> ParseJsonLayerDefs(string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw GeoServicesErrors.Invalid($"The 'layerDefs' parameter is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Object => ParseObjectLayerDefs(document.RootElement),
                JsonValueKind.Array => ParseArrayLayerDefs(document.RootElement),
                _ => throw GeoServicesErrors.Invalid(
                    "The 'layerDefs' parameter must be the simple '<layerId>:<where>' list, a JSON object of '<layerId>': '<where>', or a JSON array of {'layerId', 'where', 'outFields'}."),
            };
        }
    }

    private static Dictionary<int, LayerDef> ParseObjectLayerDefs(JsonElement root)
    {
        var defs = new Dictionary<int, LayerDef>();
        foreach (var property in root.EnumerateObject())
        {
            var layerId = RequireObjectLayerId(property.Name);
            var where = RequireObjectWhere(property, layerId);
            defs[layerId] = new LayerDef(ParseWhere(where, layerId), null);
        }

        if (defs.Count == 0)
        {
            throw GeoServicesErrors.Invalid("The 'layerDefs' parameter names no layers.");
        }

        return defs;
    }

    private static int RequireObjectLayerId(string name) =>
        int.TryParse(name, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var layerId) && layerId >= 0
            ? layerId
            : throw GeoServicesErrors.Invalid(
                $"The 'layerDefs' layer id '{name}' is not a non-negative integer.");

    private static string RequireObjectWhere(JsonProperty property, int layerId) =>
        property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString())
            ? property.Value.GetString()!
            : throw GeoServicesErrors.Invalid(
                $"The 'layerDefs' entry for layer {layerId} must be a non-empty WHERE clause string.");

    private static Dictionary<int, LayerDef> ParseArrayLayerDefs(JsonElement root)
    {
        var defs = new Dictionary<int, LayerDef>();
        foreach (var element in root.EnumerateArray())
        {
            var layerId = RequireArrayLayerId(element);
            var where = ParseArrayEntryWhere(element, layerId);
            var outFields = ParseArrayEntryOutFields(element, layerId);
            defs[layerId] = new LayerDef(where, outFields);
        }

        if (defs.Count == 0)
        {
            throw GeoServicesErrors.Invalid("The 'layerDefs' parameter names no layers.");
        }

        return defs;
    }

    /// <summary>Reads the non-negative integer <c>layerId</c> of one array-syntax <c>layerDefs</c> entry.</summary>
    private static int RequireArrayLayerId(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("layerId", out var idElement)
            && idElement.ValueKind == JsonValueKind.Number
            && idElement.TryGetInt32(out var layerId)
            && layerId >= 0)
        {
            return layerId;
        }

        throw GeoServicesErrors.Invalid(
            "Each 'layerDefs' array entry needs a non-negative integer 'layerId'.");
    }

    /// <summary>Parses the optional <c>where</c> of one array-syntax <c>layerDefs</c> entry.</summary>
    private static EsriFilterClause? ParseArrayEntryWhere(JsonElement element, int layerId)
    {
        if (!element.TryGetProperty("where", out var whereElement))
        {
            return null;
        }

        if (whereElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(whereElement.GetString()))
        {
            throw GeoServicesErrors.Invalid(
                $"The 'layerDefs' entry for layer {layerId} has a 'where' that is not a non-empty string.");
        }

        return ParseWhere(whereElement.GetString()!, layerId);
    }

    /// <summary>Parses the optional <c>outFields</c> of one array-syntax <c>layerDefs</c> entry.</summary>
    private static string[]? ParseArrayEntryOutFields(JsonElement element, int layerId)
    {
        if (!element.TryGetProperty("outFields", out var fieldsElement))
        {
            return null;
        }

        if (fieldsElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(fieldsElement.GetString()))
        {
            throw GeoServicesErrors.Invalid(
                $"The 'layerDefs' entry for layer {layerId} has an 'outFields' that is not a non-empty string.");
        }

        var outFields = fieldsElement.GetString()!
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (outFields.Length == 0)
        {
            throw GeoServicesErrors.Invalid(
                $"The 'layerDefs' entry for layer {layerId} has an 'outFields' that names no fields.");
        }

        return outFields;
    }

    private static EsriFilterClause ParseWhere(string where, int layerId)
    {
        if (!EsriFilterClause.TryParse(where, out var clause, out var error))
        {
            throw GeoServicesErrors.Invalid(
                $"The 'layerDefs' WHERE clause for layer {layerId} is not supported: {error}.");
        }

        return clause!;
    }
}

/// <summary>One layer of a service-level query: its id, description and effective query.</summary>
internal sealed record ServiceLayerQuery(
    int Id,
    DatasetDescription Description,
    EsriFeatureQuery Query,
    bool IsTable);
