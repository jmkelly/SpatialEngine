using System.Text.Json;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer <c>find</c> operation (spec §4.0.6): a literal text search
/// over the string fields of the selected layers. <c>contains</c> (default)
/// or <c>startsWith</c> selects the comparison; <c>searchFields</c> narrows
/// the fields, otherwise every string field is searched. Matches carry the
/// field that matched, the feature's attributes and its geometry.
///
/// <para>
/// The search is narrowed by the store where the plan can narrow it soundly —
/// a row can only match when a searched field carries a value — and the
/// case-insensitive comparison of the text against that value stays here
/// (ADR-0112). The text itself is not pushed: the vocabulary's only text
/// comparison is <c>LIKE</c>, which is case-sensitive on some back ends and
/// not on others, so a pushed pattern would drop rows this search has to
/// match.
/// </para>
/// </summary>
internal static class MapFindEngine
{
    public static async Task<IResult> FindAsync(
        IFeatureStore store,
        IReadOnlyList<MapLayerInfo> layers,
        EsriRequestParameters parameters,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var searchText = parameters.Require("searchText");
        var contains = parameters.GetBool("contains", true);
        var requested = ParseFields(parameters.Get("searchFields"));
        var returnGeometry = parameters.GetBool("returnGeometry", true);
        var outCrs = EsriValueParser.ParseSpatialReference(parameters.Get("sr"));
        var selected = MapLayerSelection.Select(layers, parameters.Get("layers"));
        var hits = await MatchAsync(
            store, selected, new FindQuery(searchText, contains, requested, outCrs, returnGeometry), transforms, cancellationToken);
        return EsriJson.Write(writer => WriteResults(writer, hits, returnGeometry));
    }

    /// <summary>One find request's search: the text, how it matches, the fields and the result shape.</summary>
    private sealed record FindQuery(
        string SearchText,
        bool Contains,
        IReadOnlyList<string>? Fields,
        CoordinateReference? OutCrs,
        bool ReturnGeometry);

    private static async Task<List<FindHit>> MatchAsync(
        IFeatureStore store,
        IReadOnlyList<MapLayerInfo> layers,
        FindQuery query,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var (searchText, contains, requested, outCrs, returnGeometry) = query;
        var hits = new List<FindHit>();
        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = SearchFields(layer.Dataset.Schema, requested);
            if (fields.Count == 0)
            {
                continue;
            }

            var layerCrs = EsriLayerModel.LayerCoordinateReference(layer.Dataset.Srid);
            var plan = MapMatchPushdown.Search(fields);
            var batches = (await store.QueryAsync(layer.Layer.Dataset, plan, cancellationToken)).Batches;
            foreach (var feature in batches.SelectMany(batch => batch.Features))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matched = Match(feature, fields, searchText, contains);
                if (matched is null)
                {
                    continue;
                }

                var geometry = returnGeometry ? Transform(FeatureGeometry.Find(feature), layerCrs, outCrs, transforms, cancellationToken) : null;
                hits.Add(new FindHit(layer, feature, matched.Value.Field, geometry));
            }
        }

        return hits;
    }

    private static (string Field, string Value)? Match(Feature feature, IReadOnlyList<string> fields, string searchText, bool contains)
    {
        foreach (var field in fields)
        {
            if (MapFeatures.StringValue(feature, field) is not { } value)
            {
                continue;
            }

            var hit = contains
                ? value.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                : value.StartsWith(searchText, StringComparison.OrdinalIgnoreCase);
            if (hit)
            {
                return (field, value);
            }
        }

        return null;
    }

    private static List<string> SearchFields(FeatureSchema schema, IReadOnlyList<string>? requested)
    {
        var fields = new List<string>();
        if (requested is null)
        {
            for (var i = 0; i < schema.Count; i++)
            {
                if (schema[i].Kind == AttributeKind.String)
                {
                    fields.Add(schema[i].Name);
                }
            }

            return fields;
        }

        foreach (var name in requested)
        {
            var index = schema.IndexOf(name);
            if (index >= 0 && schema[index].Kind == AttributeKind.String)
            {
                fields.Add(name);
            }
        }

        return fields;
    }

    private static string[]? ParseFields(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static void WriteResults(Utf8JsonWriter writer, IReadOnlyList<FindHit> hits, bool returnGeometry)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("results");
        writer.WriteStartArray();
        foreach (var hit in hits)
        {
            writer.WriteStartObject();
            writer.WriteNumber("layerId", hit.Layer.Layer.Id);
            writer.WriteString("layerName", hit.Layer.Layer.Name);
            writer.WriteString("foundFieldName", hit.Field);
            writer.WriteString("value", MapFeatures.StringValue(hit.Feature, hit.Field));
            writer.WritePropertyName("attributes");
            writer.WriteStartObject();
            MapFeatures.WriteAttributes(writer, hit.Feature);
            writer.WriteEndObject();
            writer.WriteString("geometryType", EsriLayerModel.GeometryType(hit.Layer.Dataset.GeometryType));
            if (returnGeometry && hit.Geometry is { } geometry)
            {
                writer.WritePropertyName("geometry");
                EsriGeometryCodec.Write(writer, geometry);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static IGeometry? Transform(IGeometry? geometry, CoordinateReference? source, CoordinateReference? target, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
        geometry is not null && target is { } to && source is { } from && from != to
            ? transforms.Transform(geometry, from.ToString(), to.ToString(), cancellationToken)
            : geometry;

    private sealed record FindHit(MapLayerInfo Layer, Feature Feature, string Field, IGeometry? Geometry);
}
