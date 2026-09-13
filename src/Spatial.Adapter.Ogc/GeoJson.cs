using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// A minimal, core-typed GeoJSON writer (ADR-0052 §3): the engine has no
/// GeoJSON encoder, so WFS writes GeoJSON features and geometry coordinates
/// here directly from <see cref="IGeometry"/> values. No third-party type
/// crosses the boundary; the primary geometry column becomes the feature's
/// <c>geometry</c> and the remaining attributes become <c>properties</c>.
/// </summary>
internal static class GeoJson
{
    /// <summary>Serialises a set of features, each with its own dataset description, as a FeatureCollection.</summary>
    public static byte[] FeatureCollection(IReadOnlyList<(DatasetDescription Dataset, Feature Feature)> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            writer.WriteStartArray("features");
            foreach (var (dataset, feature) in features)
            {
                WriteFeature(writer, dataset, feature);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Writes one core geometry as a GeoJSON geometry object.</summary>
    public static void WriteGeometry(Utf8JsonWriter writer, IGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(geometry);
        writer.WriteStartObject();
        switch (geometry)
        {
            case Point point:
                writer.WriteString("type", "Point");
                writer.WritePropertyName("coordinates");
                WritePosition(writer, point.Coordinate);
                break;
            case MultiPoint multiPoint:
                writer.WriteString("type", "MultiPoint");
                writer.WritePropertyName("coordinates");
                WritePositions(writer, multiPoint.Points.Select(item => item.Coordinate));
                break;
            case LineString line:
                writer.WriteString("type", "LineString");
                writer.WritePropertyName("coordinates");
                WriteSequence(writer, line.Sequence);
                break;
            case MultiLineString multiLine:
                writer.WriteString("type", "MultiLineString");
                writer.WritePropertyName("coordinates");
                writer.WriteStartArray();
                foreach (var part in multiLine.LineStrings)
                {
                    WriteSequence(writer, part.Sequence);
                }

                writer.WriteEndArray();
                break;
            case Polygon polygon:
                writer.WriteString("type", "Polygon");
                writer.WritePropertyName("coordinates");
                WriteRings(writer, polygon);
                break;
            case MultiPolygon multiPolygon:
                writer.WriteString("type", "MultiPolygon");
                writer.WritePropertyName("coordinates");
                writer.WriteStartArray();
                foreach (var part in multiPolygon.Polygons)
                {
                    WriteRings(writer, part);
                }

                writer.WriteEndArray();
                break;
            case IGeometryParts parts:
                writer.WriteString("type", "GeometryCollection");
                writer.WritePropertyName("geometries");
                writer.WriteStartArray();
                foreach (var part in parts.Geometries)
                {
                    WriteGeometry(writer, part);
                }

                writer.WriteEndArray();
                break;
            default:
                writer.WriteString("type", "Point");
                writer.WriteNull("coordinates");
                break;
        }

        writer.WriteEndObject();
    }

    private static void WriteFeature(Utf8JsonWriter writer, DatasetDescription dataset, Feature feature)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "Feature");
        writer.WriteString("id", feature.Id.Value);
        var geometryIndex = dataset.Schema.IndexOf(dataset.GeometryColumn);
        writer.WriteStartObject("properties");
        for (var index = 0; index < feature.Schema.Count; index++)
        {
            if (index == geometryIndex && feature.Schema[index].Kind == AttributeKind.Geometry)
            {
                continue;
            }

            writer.WritePropertyName(feature.Schema[index].Name);
            WriteValue(writer, feature[index]);
        }

        writer.WriteEndObject();
        writer.WritePropertyName("geometry");
        if (geometryIndex >= 0
            && feature[geometryIndex].Kind == AttributeKind.Geometry
            && !feature[geometryIndex].IsNull)
        {
            WriteGeometry(writer, feature[geometryIndex].GeometryValue);
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, AttributeValue value)
    {
        switch (value.Kind)
        {
            case AttributeKind.Boolean:
                writer.WriteBooleanValue(value.BooleanValue);
                break;
            case AttributeKind.Int64:
                writer.WriteNumberValue(value.Int64Value);
                break;
            case AttributeKind.Double when double.IsFinite(value.DoubleValue):
                writer.WriteNumberValue(value.DoubleValue);
                break;
            case AttributeKind.String:
                writer.WriteStringValue(value.StringValue);
                break;
            case AttributeKind.DateTimeOffset:
                writer.WriteStringValue(value.DateTimeOffsetValue);
                break;
            case AttributeKind.Guid:
                writer.WriteStringValue(value.GuidValue);
                break;
            case AttributeKind.Geometry when !value.IsNull:
                WriteGeometry(writer, value.GeometryValue);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static void WriteSequence(Utf8JsonWriter writer, ICoordinateSequence sequence)
    {
        writer.WriteStartArray();
        for (var index = 0; index < sequence.Count; index++)
        {
            WritePosition(writer, sequence.GetCoordinate(index));
        }

        writer.WriteEndArray();
    }

    private static void WritePositions(Utf8JsonWriter writer, IEnumerable<Coordinate?> coordinates)
    {
        writer.WriteStartArray();
        foreach (var coordinate in coordinates)
        {
            WritePosition(writer, coordinate);
        }

        writer.WriteEndArray();
    }

    private static void WritePosition(Utf8JsonWriter writer, Coordinate? coordinate)
    {
        if (coordinate is not { } value)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        if (value.Z is { } z && double.IsFinite(z))
        {
            writer.WriteNumberValue(z);
        }

        writer.WriteEndArray();
    }

    private static void WriteRings(Utf8JsonWriter writer, Polygon polygon)
    {
        writer.WriteStartArray();
        WriteSequence(writer, polygon.ExteriorRing.Sequence);
        foreach (var ring in polygon.InteriorRings)
        {
            WriteSequence(writer, ring.Sequence);
        }

        writer.WriteEndArray();
    }
}
