using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Interop.Esri;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Attribute helpers shared by the MapServer identify and find engines: a
/// feature's non-geometry attributes are written into an Esri
/// <c>attributes</c> object, and a display value is read from a named field.
/// </summary>
internal static class MapFeatures
{
    /// <summary>Writes every non-geometry attribute as an Esri attribute entry.</summary>
    public static void WriteAttributes(Utf8JsonWriter writer, Feature feature)
    {
        var geometryIndex = FeatureGeometry.Index(feature.Schema);
        for (var i = 0; i < feature.Schema.Count; i++)
        {
            if (i != geometryIndex)
            {
                EsriAttributeCodec.Write(writer, feature.Schema[i].Name, feature[i]);
            }
        }
    }

    /// <summary>The string value of a named field, or null when absent or not a string.</summary>
    public static string? StringValue(Feature feature, string field)
    {
        var index = feature.Schema.IndexOf(field);
        return index >= 0 && feature[index].Kind == AttributeKind.String ? feature[index].StringValue : null;
    }

    /// <summary>The first string-typed field name of a schema, or empty when there is none.</summary>
    public static string DisplayField(FeatureSchema schema)
    {
        for (var i = 0; i < schema.Count; i++)
        {
            if (schema[i].Kind == AttributeKind.String)
            {
                return schema[i].Name;
            }
        }

        return string.Empty;
    }
}
