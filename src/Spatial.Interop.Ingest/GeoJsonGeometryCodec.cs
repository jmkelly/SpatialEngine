using System.Text.Json;
using Spatial.Core.Geometry;

namespace Spatial.Interop.Ingest;

/// <summary>
/// Decodes GeoJSON geometry objects (RFC 7946) into core geometry values,
/// stamping every geometry with the decode CRS. Positions are x-first, which
/// matches the core convention. Empty coordinate arrays become the
/// corresponding empty geometry; an absent or null geometry is the caller's
/// concern (a null attribute).
/// </summary>
internal static class GeoJsonGeometryCodec
{
    private static readonly Dictionary<string, Func<JsonElement, CoordinateReference, IGeometry>> Readers =
        new(StringComparer.Ordinal)
        {
            ["Point"] = ReadPoint,
            ["LineString"] = ReadLineString,
            ["Polygon"] = ReadPolygon,
            ["MultiPoint"] = ReadMultiPoint,
            ["MultiLineString"] = ReadMultiLineString,
            ["MultiPolygon"] = ReadMultiPolygon,
            ["GeometryCollection"] = ReadCollection,
        };

    /// <summary>Decodes one GeoJSON geometry object.</summary>
    public static IGeometry Decode(JsonElement element, CoordinateReference crs)
    {
        var type = GeometryType(element);
        if (Readers.TryGetValue(type, out var reader))
        {
            return reader(element, crs);
        }

        throw new IngestFormatException($"Unsupported GeoJSON geometry type '{type}'.");
    }

    private static string GeometryType(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || element.TryGetProperty("type", out var typeElement) is false
            || typeElement.ValueKind != JsonValueKind.String)
        {
            throw new IngestFormatException("A GeoJSON geometry needs a string 'type'.");
        }

        return typeElement.GetString()!;
    }

    private static Polygon ReadPolygon(JsonElement element, CoordinateReference crs) => BuildPolygon(Parts(element), crs);

    private static Point ReadPoint(JsonElement element, CoordinateReference crs)
    {
        var coordinates = Coordinates(element);
        return coordinates.GetArrayLength() == 0
            ? GeometryFactory.CreateEmptyPoint(crs)
            : GeometryFactory.CreatePoint(Position(coordinates), crs);
    }

    private static LineString ReadLineString(JsonElement element, CoordinateReference crs) =>
        ToLineString(Positions(Coordinates(element)), crs);

    private static MultiPoint ReadMultiPoint(JsonElement element, CoordinateReference crs)
    {
        var points = Positions(Coordinates(element)).Select(position => GeometryFactory.CreatePoint(position, crs)).ToArray();
        return GeometryFactory.CreateMultiPoint((IEnumerable<Point>)points, crs);
    }

    private static MultiLineString ReadMultiLineString(JsonElement element, CoordinateReference crs)
    {
        var lines = Parts(element).Select(part => ToLineString(part, crs)).ToArray();
        return GeometryFactory.CreateMultiLineString(lines, crs);
    }

    private static MultiPolygon ReadMultiPolygon(JsonElement element, CoordinateReference crs)
    {
        var polygons = new List<Polygon>();
        foreach (var polygon in Coordinates(element).EnumerateArray())
        {
            if (polygon.ValueKind != JsonValueKind.Array)
            {
                throw new IngestFormatException("A GeoJSON MultiPolygon coordinate must be an array of rings.");
            }

            polygons.Add(BuildPolygon(FromArray(polygon), crs));
        }

        return GeometryFactory.CreateMultiPolygon(polygons, crs);
    }

    private static GeometryCollection ReadCollection(JsonElement element, CoordinateReference crs)
    {
        if (!element.TryGetProperty("geometries", out var geometries) || geometries.ValueKind != JsonValueKind.Array)
        {
            throw new IngestFormatException("A GeoJSON GeometryCollection needs an array 'geometries'.");
        }

        var parts = new List<IGeometry>();
        foreach (var geometry in geometries.EnumerateArray())
        {
            parts.Add(Decode(geometry, crs));
        }

        return GeometryFactory.CreateGeometryCollection(parts, crs);
    }

    /// <summary>The <c>coordinates</c> array of a GeoJSON geometry.</summary>
    private static JsonElement Coordinates(JsonElement element)
    {
        if (!element.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array)
        {
            throw new IngestFormatException("A GeoJSON geometry needs an array 'coordinates'.");
        }

        return coordinates;
    }

    /// <summary>The rings (or parts) of a polygon- or multi-line-shaped <c>coordinates</c> array.</summary>
    private static List<Coordinate[]> Parts(JsonElement element) => FromArray(Coordinates(element));

    private static List<Coordinate[]> FromArray(JsonElement array)
    {
        var parts = new List<Coordinate[]>();
        foreach (var part in array.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Array)
            {
                throw new IngestFormatException("A GeoJSON nested coordinate must be an array.");
            }

            parts.Add(Positions(part));
        }

        return parts;
    }

    private static Coordinate[] Positions(JsonElement array)
    {
        var positions = new List<Coordinate>();
        foreach (var position in array.EnumerateArray())
        {
            positions.Add(Position(position));
        }

        return positions.ToArray();
    }

    private static Coordinate Position(JsonElement position)
    {
        if (position.ValueKind != JsonValueKind.Array)
        {
            throw new IngestFormatException("A GeoJSON position must be an array.");
        }

        Span<double> ordinates = stackalloc double[3];
        var count = 0;
        foreach (var ordinate in position.EnumerateArray())
        {
            if (count == 3)
            {
                break;
            }

            if (ordinate.ValueKind != JsonValueKind.Number)
            {
                throw new IngestFormatException("A GeoJSON position ordinate must be a number.");
            }

            ordinates[count++] = ordinate.GetDouble();
        }

        if (count < 2)
        {
            throw new IngestFormatException("A GeoJSON position needs at least an x and a y.");
        }

        return count == 3 ? new Coordinate(ordinates[0], ordinates[1], ordinates[2]) : new Coordinate(ordinates[0], ordinates[1]);
    }

    private static Polygon BuildPolygon(List<Coordinate[]> rings, CoordinateReference crs)
    {
        if (rings.Count == 0)
        {
            return GeometryFactory.CreatePolygon(ReadOnlySpan<Coordinate>.Empty, crs);
        }

        var exterior = ToLineString(rings[0], crs);
        var interiors = rings.Skip(1).Select(ring => ToLineString(ring, crs));
        return GeometryFactory.CreatePolygon(exterior, interiors, crs);
    }

    private static LineString ToLineString(Coordinate[] coordinates, CoordinateReference crs) =>
        coordinates.Length == 0
            ? GeometryFactory.CreateEmptyLineString(CoordinateLayout.Xy, crs)
            : GeometryFactory.CreateLineString(coordinates, crs);
}
