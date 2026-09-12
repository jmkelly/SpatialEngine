using System.Globalization;
using System.Text.Json;
using Spatial.Core.Geometry;

namespace Spatial.Interop.Esri;

/// <summary>
/// The Esri JSON geometry codec (spec §10): point <c>{x,y}</c>, multipoint
/// <c>{points}</c>, polyline <c>{paths}</c>, polygon <c>{rings}</c> and
/// envelope <c>{xmin..ymax}</c> ↔ core geometry values, with the
/// <c>spatialReference</c> object and optional Z/M ordinates. The
/// <c>{"url": ...}</c> remote-input form is rejected (SSRF, ADR-0035 §3).
/// </summary>
public static class EsriGeometryCodec
{
    /// <summary>Decodes an Esri geometry object using its own spatial reference.</summary>
    public static IGeometry Decode(JsonElement element) => Decode(element, fallback: null);

    /// <summary>
    /// Decodes an Esri geometry object; the element's own spatial reference
    /// wins, otherwise <paramref name="fallback"/> applies (for example a
    /// layer's SRID or an <c>inSR</c> request parameter).
    /// </summary>
    public static IGeometry Decode(JsonElement element, CoordinateReference? fallback)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw EsriInteropException.Invalid("An Esri geometry must be a JSON object.");
        }

        if (element.TryGetProperty("url", out _))
        {
            throw EsriInteropException.Invalid(
                "The {\"url\": ...} remote geometry form is not supported; send the geometry inline (no server-side fetch).");
        }

        var crs = EsriSpatialReference.DecodeFrom(element) ?? fallback;
        var flags = GeometryFlags.From(element);
        if (element.TryGetProperty("x", out _) && element.TryGetProperty("y", out _))
        {
            return DecodePoint(element, crs, flags);
        }

        if (element.TryGetProperty("points", out var points))
        {
            return DecodeMultiPoint(points, crs, flags);
        }

        if (element.TryGetProperty("paths", out var paths))
        {
            return DecodePolyline(paths, crs, flags);
        }

        if (element.TryGetProperty("rings", out var rings))
        {
            return DecodePolygon(rings, crs, flags);
        }

        if (element.TryGetProperty("xmin", out _))
        {
            return DecodeEnvelope(element, crs);
        }

        throw EsriInteropException.Invalid(
            "The geometry object carries none of the Esri shapes: point {x,y}, {points}, {paths}, {rings} or {xmin..ymax}.");
    }

    /// <summary>
    /// Parses the comma-separated simple geometry syntax the spec allows in
    /// request parameters: <c>x,y</c> (point) or
    /// <c>xmin,ymin,xmax,ymax</c> (envelope).
    /// </summary>
    public static bool TryParseSimple(string? text, out IGeometry? geometry)
    {
        geometry = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is not (2 or 4) || !parts.All(IsFiniteNumber))
        {
            return false;
        }

        geometry = parts.Length == 2
            ? GeometryFactory.CreatePoint(Number(parts[0]), Number(parts[1]))
            : EnvelopePolygon(Number(parts[0]), Number(parts[1]), Number(parts[2]), Number(parts[3]));
        return true;
    }

    /// <summary>Encodes a core geometry as an Esri JSON string.</summary>
    public static string Encode(IGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, geometry);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Writes a core geometry as an Esri JSON object.</summary>
    public static void Write(Utf8JsonWriter writer, IGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(geometry);

        writer.WriteStartObject();
        switch (geometry)
        {
            case Point point:
                WritePoint(writer, point);
                break;
            case MultiPoint multiPoint:
                WriteCoordinates(writer, "points", multiPoint.Points.Select(item => item.Coordinate), multiPoint.Layout);
                break;
            case LineString line:
                WritePaths(writer, [line.Sequence]);
                break;
            case MultiLineString multiLine:
                WritePaths(writer, multiLine.LineStrings.Select(item => item.Sequence));
                break;
            case Polygon polygon:
                WriteRings(writer, [polygon]);
                break;
            case MultiPolygon multiPolygon:
                WriteRings(writer, multiPolygon.Polygons);
                break;
            default:
                throw EsriInteropException.Invalid(
                    $"Core geometry type '{geometry.Type}' has no Esri JSON representation.");
        }

        EsriSpatialReference.Write(writer, geometry.CoordinateReference);
        writer.WriteEndObject();
    }

    private static Point DecodePoint(JsonElement element, CoordinateReference? crs, GeometryFlags flags)
    {
        var coordinate = new Coordinate(
            Number(element.GetProperty("x")),
            Number(element.GetProperty("y")),
            ReadOrdinate(element, "z", flags.HasZ),
            ReadOrdinate(element, "m", flags.HasM));
        return GeometryFactory.CreatePoint(coordinate, crs);
    }

    private static MultiPoint DecodeMultiPoint(JsonElement points, CoordinateReference? crs, GeometryFlags flags)
    {
        var coordinates = ReadCoordinates(points, flags);
        return GeometryFactory.CreateMultiPoint(coordinates.Select(coordinate => GeometryFactory.CreatePoint(coordinate)), crs);
    }

    private static IGeometry DecodePolyline(JsonElement paths, CoordinateReference? crs, GeometryFlags flags)
    {
        var lines = ReadParts(paths, flags).Select(sequence => GeometryFactory.CreateLineString(sequence, crs)).ToArray();
        return lines.Length == 1
            ? lines[0]
            : GeometryFactory.CreateMultiLineString(lines, crs);
    }

    private static IGeometry DecodePolygon(JsonElement rings, CoordinateReference? crs, GeometryFlags flags)
    {
        var parts = ReadParts(rings, flags);
        if (parts.Count == 0)
        {
            return GeometryFactory.CreatePolygon(Array.Empty<Coordinate>(), crs);
        }

        var polygons = GroupRings(parts)
            .Select(group => GeometryFactory.CreatePolygon(
                GeometryFactory.CreateLineString(group.Outer),
                group.Holes.Select(hole => GeometryFactory.CreateLineString(hole)),
                crs))
            .ToArray();
        return polygons.Length == 1
            ? polygons[0]
            : GeometryFactory.CreateMultiPolygon(polygons, crs);
    }

    private static Polygon DecodeEnvelope(JsonElement element, CoordinateReference? crs) =>
        EnvelopePolygon(
            Number(element.GetProperty("xmin")),
            Number(element.GetProperty("ymin")),
            Number(element.GetProperty("xmax")),
            Number(element.GetProperty("ymax")),
            crs);

    private static Polygon EnvelopePolygon(double minX, double minY, double maxX, double maxY, CoordinateReference? crs = null)
    {
        if (maxX < minX || maxY < minY)
        {
            throw EsriInteropException.Invalid(
                FormattableString.Invariant($"Invalid envelope: ({minX}, {minY}) exceeds ({maxX}, {maxY})."));
        }

        return GeometryFactory.CreatePolygon(
            [
                new Coordinate(minX, minY),
                new Coordinate(maxX, minY),
                new Coordinate(maxX, maxY),
                new Coordinate(minX, maxY),
                new Coordinate(minX, minY),
            ],
            crs);
    }

    private static void WritePoint(Utf8JsonWriter writer, Point point)
    {
        if (point.Coordinate is not { } coordinate)
        {
            return;
        }

        writer.WriteNumber("x", coordinate.X);
        writer.WriteNumber("y", coordinate.Y);
        if (coordinate.Z is { } z)
        {
            writer.WriteNumber("z", z);
        }

        if (coordinate.M is { } m)
        {
            writer.WriteNumber("m", m);
        }
    }

    private static void WriteCoordinates(Utf8JsonWriter writer, string property, IEnumerable<Coordinate?> coordinates, CoordinateLayout layout)
    {
        writer.WritePropertyName(property);
        writer.WriteStartArray();
        foreach (var coordinate in coordinates)
        {
            if (coordinate is { } value)
            {
                WriteCoordinate(writer, value, layout);
            }
        }

        writer.WriteEndArray();
    }

    private static void WritePaths(Utf8JsonWriter writer, IEnumerable<ICoordinateSequence> parts)
    {
        writer.WritePropertyName("paths");
        writer.WriteStartArray();
        foreach (var part in parts)
        {
            WriteSequence(writer, part, part.Layout);
        }

        writer.WriteEndArray();
    }

    private static void WriteRings(Utf8JsonWriter writer, IEnumerable<Polygon> polygons)
    {
        writer.WritePropertyName("rings");
        writer.WriteStartArray();
        foreach (var polygon in polygons)
        {
            WriteSequence(writer, polygon.ExteriorRing.Sequence, polygon.ExteriorRing.Layout);
            foreach (var hole in polygon.InteriorRings)
            {
                WriteSequence(writer, hole.Sequence, hole.Layout);
            }
        }

        writer.WriteEndArray();
    }

    private static void WriteSequence(Utf8JsonWriter writer, ICoordinateSequence sequence, CoordinateLayout layout)
    {
        writer.WriteStartArray();
        for (var i = 0; i < sequence.Count; i++)
        {
            WriteCoordinate(writer, sequence.GetCoordinate(i), layout);
        }

        writer.WriteEndArray();
    }

    private static void WriteCoordinate(Utf8JsonWriter writer, Coordinate coordinate, CoordinateLayout layout)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(coordinate.X);
        writer.WriteNumberValue(coordinate.Y);
        if (layout.HasZ())
        {
            writer.WriteNumberValue(coordinate.Z ?? double.NaN);
        }

        if (layout.HasM())
        {
            writer.WriteNumberValue(coordinate.M ?? double.NaN);
        }

        writer.WriteEndArray();
    }

    private static List<Coordinate> ReadCoordinates(JsonElement element, GeometryFlags flags)
    {
        var coordinates = new List<Coordinate>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            coordinates.Add(ReadCoordinate(item, flags));
        }

        return coordinates;
    }

    private static List<Coordinate[]> ReadParts(JsonElement element, GeometryFlags flags)
    {
        var parts = new List<Coordinate[]>(element.GetArrayLength());
        foreach (var part in element.EnumerateArray())
        {
            parts.Add(ReadCoordinates(part, flags).ToArray());
        }

        return parts;
    }

    private static Coordinate ReadCoordinate(JsonElement element, GeometryFlags flags)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw EsriInteropException.Invalid("An Esri coordinate must be an array of numbers.");
        }

        var values = element.EnumerateArray().Select(Number).ToArray();
        return values.Length switch
        {
            2 => new Coordinate(values[0], values[1]),
            3 => CoordinateFrom3(values, flags),
            4 => new Coordinate(values[0], values[1], values[2], values[3]),
            _ => throw EsriInteropException.Invalid(
                $"An Esri coordinate array must hold 2, 3 or 4 numbers (x, y, z, m); got {values.Length}."),
        };
    }

    private static Coordinate CoordinateFrom3(double[] values, GeometryFlags flags)
    {
        return flags.HasM && !flags.HasZ
            ? new Coordinate(values[0], values[1], null, values[2])
            : new Coordinate(values[0], values[1], values[2], null);
    }

    private static double? ReadOrdinate(JsonElement element, string property, bool present)
    {
        if (!present || !element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.GetDouble();
    }

    private static double Number(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value))
        {
            return value;
        }

        throw EsriInteropException.Invalid("Expected a JSON number in an Esri geometry, got a non-number value.");
    }

    private static bool IsFiniteNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value);

    private static double Number(string text) =>
        double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>
    /// Groups polygon rings into exterior/hole sets using the Esri
    /// orientation convention (exterior clockwise, holes counter-clockwise).
    /// A ring whose orientation is neutral or that appears before any
    /// exterior starts a new exterior, so non-conforming data still decodes.
    /// </summary>
    private static List<(Coordinate[] Outer, List<Coordinate[]> Holes)> GroupRings(List<Coordinate[]> rings)
    {
        var polygons = new List<(Coordinate[] Outer, List<Coordinate[]> Holes)>();
        foreach (var ring in rings)
        {
            var isHole = polygons.Count > 0 && ShoelaceArea(ring) < 0;
            if (isHole)
            {
                polygons[^1].Holes.Add(ring);
            }
            else
            {
                polygons.Add((ring, []));
            }
        }

        return polygons;
    }

    /// <summary>Twice the signed ring area; positive means clockwise (an Esri exterior ring), negative counter-clockwise (an Esri hole).</summary>
    private static double ShoelaceArea(Coordinate[] ring)
    {
        var sum = 0.0;
        for (var i = 0; i < ring.Length - 1; i++)
        {
            sum += (ring[i + 1].X - ring[i].X) * (ring[i + 1].Y + ring[i].Y);
        }

        return sum;
    }

    /// <summary>The <c>hasZ</c>/<c>hasM</c> flags an Esri geometry object may carry.</summary>
    private readonly record struct GeometryFlags(bool HasZ, bool HasM)
    {
        public static GeometryFlags From(JsonElement element) =>
            new(Flag(element, "hasZ"), Flag(element, "hasM"));

        private static bool Flag(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }
}
