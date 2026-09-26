using System.Text.Json;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer identify result projection (spec §4.0.5): one Esri JSON
/// result per hit with the layer's display field value, the feature's
/// attributes, the geometry type and — when <c>returnGeometry</c> asked for
/// it — the hit geometry. Split out of <see cref="MapIdentifyEngine"/> so
/// the wire projection is written apart from the match pipeline.
/// </summary>
internal static class MapIdentifyWriter
{
    /// <summary>Writes the <c>results</c> array of an identify response.</summary>
    public static void WriteResults(Utf8JsonWriter writer, IReadOnlyList<IdentifyHit> hits, bool returnGeometry)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("results");
        writer.WriteStartArray();
        foreach (var hit in hits)
        {
            var displayField = MapFeatures.DisplayField(hit.Layer.Dataset.Schema);
            writer.WriteStartObject();
            writer.WriteNumber("layerId", hit.Layer.Layer.Id);
            writer.WriteString("layerName", hit.Layer.Layer.Name);
            writer.WriteString("displayFieldName", displayField);
            writer.WriteString("value", displayField.Length > 0 ? MapFeatures.StringValue(hit.Feature, displayField) : null);
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
}
