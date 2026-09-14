using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// A minimal, core-typed GeoJSON writer (ADR-0053 §3): the engine has no
/// GeoJSON encoder, so WFS writes GeoJSON features and geometry coordinates
/// here directly from <see cref="IGeometry"/> values. No third-party type
/// crosses the boundary; the primary geometry column becomes the feature's
/// <c>geometry</c> and the remaining attributes become <c>properties</c>.
///
/// <para>Attribute values and geometry families dispatch through small
/// per-kind/-family writers, so each unit stays cohesive and low-complexity
/// (ADR-0040).</para>
/// </summary>
internal static class GeoJson
{
    private static readonly Dictionary<AttributeKind, Action<Utf8JsonWriter, AttributeValue>> ValueWriters = new()
    {
        [AttributeKind.Boolean] = (writer, value) => writer.WriteBooleanValue(value.BooleanValue),
        [AttributeKind.Int64] = (writer, value) => writer.WriteNumberValue(value.Int64Value),
        [AttributeKind.Double] = WriteDoubleValue,
        [AttributeKind.String] = (writer, value) => writer.WriteStringValue(value.StringValue),
        [AttributeKind.DateTimeOffset] = (writer, value) => writer.WriteStringValue(value.DateTimeOffsetValue),
        [AttributeKind.Guid] = (writer, value) => writer.WriteStringValue(value.GuidValue),
        [AttributeKind.Geometry] = WriteGeometryValue,
    };

    private static readonly Dictionary<Type, Action<Utf8JsonWriter, IGeometry>> GeometryWriters = new()
    {
        [typeof(Point)] = (writer, geometry) => WritePoint(writer, (Point)geometry),
        [typeof(MultiPoint)] = (writer, geometry) => WriteMultiPoint(writer, (MultiPoint)geometry),
        [typeof(LineString)] = (writer, geometry) => WriteLineString(writer, (LineString)geometry),
        [typeof(MultiLineString)] = (writer, geometry) => WriteMultiLineString(writer, (MultiLineString)geometry),
        [typeof(Polygon)] = (writer, geometry) => WritePolygon(writer, (Polygon)geometry),
        [typeof(MultiPolygon)] = (writer, geometry) => WriteMultiPolygon(writer, (MultiPolygon)geometry),
        [typeof(GeometryCollection)] = (writer, geometry) => WriteCollection(writer, (GeometryCollection)geometry),
    };

    /// <summary>
    /// Serialises a set of features, each with its own dataset description, as a FeatureCollection.
    /// The WFS paging envelope (<c>numberMatched</c>, <c>numberReturned</c> and,
    /// while features remain, <c>next</c>) is written only when the caller passes
    /// it, so the WMS identify shape stays unchanged while a WFS client paging
    /// with <c>startIndex</c> can terminate without a further request.
    /// </summary>
    public static byte[] FeatureCollection(
        IReadOnlyList<(DatasetDescription Dataset, Feature Feature)> features,
        int? numberMatched = null,
        int? numberReturned = null,
        string? next = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        return WriteCollection(
            features.Select(item => (Id: item.Feature.Id.Value, item.Dataset, item.Feature)).ToArray(),
            numberMatched,
            numberReturned,
            next);
    }

    /// <summary>
    /// Serialises a WFS GetFeature response: each feature id is scoped per
    /// typeName as <c>&lt;typeName&gt;.&lt;id&gt;</c> (ADR-0064), so a
    /// multi-typename collection carries no duplicate ids and a genuine
    /// client (OpenLayers drops same-id features) addresses every feature.
    /// The WMS identify shape keeps the raw store ids; only the WFS path
    /// qualifies.
    /// </summary>
    public static byte[] WfsFeatureCollection(
        IReadOnlyList<(string TypeName, DatasetDescription Dataset, Feature Feature)> features,
        int? numberMatched = null,
        int? numberReturned = null,
        string? next = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        return WriteCollection(
            features.Select(item => (Id: $"{item.TypeName}.{item.Feature.Id.Value}", item.Dataset, item.Feature)).ToArray(),
            numberMatched,
            numberReturned,
            next);
    }

    private static byte[] WriteCollection(
        IReadOnlyList<(string Id, DatasetDescription Dataset, Feature Feature)> features,
        int? numberMatched,
        int? numberReturned,
        string? next)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            if (numberMatched.HasValue)
            {
                writer.WriteNumber("numberMatched", numberMatched.Value);
            }

            if (numberReturned.HasValue)
            {
                writer.WriteNumber("numberReturned", numberReturned.Value);
            }

            if (next is not null)
            {
                writer.WriteString("next", next);
            }

            writer.WriteStartArray("features");
            foreach (var (id, dataset, feature) in features)
            {
                WriteFeature(writer, id, dataset, feature);
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
        if (GeometryWriters.TryGetValue(geometry.GetType(), out var write))
        {
            write(writer, geometry);
        }
        else
        {
            WriteNullPoint(writer);
        }

        writer.WriteEndObject();
    }

    /// <summary>Writes one attribute value as a JSON value, mapping null/unknown kinds to JSON null.</summary>
    internal static void WriteValue(Utf8JsonWriter writer, AttributeValue value)
    {
        if (value.IsNull || !ValueWriters.TryGetValue(value.Kind, out var write))
        {
            writer.WriteNullValue();
            return;
        }

        write(writer, value);
    }

    private static void WriteFeature(Utf8JsonWriter writer, string id, DatasetDescription dataset, Feature feature)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "Feature");
        writer.WriteString("id", id);
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
        WriteFeatureGeometry(writer, feature, geometryIndex);
        writer.WriteEndObject();
    }

    private static void WriteFeatureGeometry(Utf8JsonWriter writer, Feature feature, int geometryIndex)
    {
        if (geometryIndex >= 0
            && feature[geometryIndex].Kind == AttributeKind.Geometry
            && !feature[geometryIndex].IsNull)
        {
            WriteGeometry(writer, feature[geometryIndex].GeometryValue);
            return;
        }

        writer.WriteNullValue();
    }

    private static void WriteDoubleValue(Utf8JsonWriter writer, AttributeValue value)
    {
        if (double.IsFinite(value.DoubleValue))
        {
            writer.WriteNumberValue(value.DoubleValue);
            return;
        }

        writer.WriteNullValue();
    }

    private static void WriteGeometryValue(Utf8JsonWriter writer, AttributeValue value) =>
        WriteGeometry(writer, value.GeometryValue);

    private static void WritePoint(Utf8JsonWriter writer, Point point)
    {
        writer.WriteString("type", "Point");
        writer.WritePropertyName("coordinates");
        WritePosition(writer, point.Coordinate);
    }

    private static void WriteMultiPoint(Utf8JsonWriter writer, MultiPoint multiPoint)
    {
        writer.WriteString("type", "MultiPoint");
        writer.WritePropertyName("coordinates");
        WritePositions(writer, multiPoint.Points.Select(item => item.Coordinate));
    }

    private static void WriteLineString(Utf8JsonWriter writer, LineString line)
    {
        writer.WriteString("type", "LineString");
        writer.WritePropertyName("coordinates");
        WriteSequence(writer, line.Sequence);
    }

    private static void WriteMultiLineString(Utf8JsonWriter writer, MultiLineString multiLine)
    {
        writer.WriteString("type", "MultiLineString");
        writer.WritePropertyName("coordinates");
        writer.WriteStartArray();
        foreach (var part in multiLine.LineStrings)
        {
            WriteSequence(writer, part.Sequence);
        }

        writer.WriteEndArray();
    }

    private static void WritePolygon(Utf8JsonWriter writer, Polygon polygon)
    {
        writer.WriteString("type", "Polygon");
        writer.WritePropertyName("coordinates");
        WriteRings(writer, polygon);
    }

    private static void WriteMultiPolygon(Utf8JsonWriter writer, MultiPolygon multiPolygon)
    {
        writer.WriteString("type", "MultiPolygon");
        writer.WritePropertyName("coordinates");
        writer.WriteStartArray();
        foreach (var part in multiPolygon.Polygons)
        {
            WriteRings(writer, part);
        }

        writer.WriteEndArray();
    }

    private static void WriteCollection(Utf8JsonWriter writer, GeometryCollection collection)
    {
        writer.WriteString("type", "GeometryCollection");
        writer.WritePropertyName("geometries");
        writer.WriteStartArray();
        foreach (var part in collection.Geometries)
        {
            WriteGeometry(writer, part);
        }

        writer.WriteEndArray();
    }

    private static void WriteNullPoint(Utf8JsonWriter writer)
    {
        writer.WriteString("type", "Point");
        writer.WriteNull("coordinates");
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
