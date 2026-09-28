using System.Text.Json;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The <c>quantizationParameters</c> response grid (spec §9.1.4): the client
/// names a tolerance and the view extent, and the response carries every
/// ordinate snapped to that grid. Served rather than rejected since the
/// generalization verb the quantum needs exists (SpatialEngine-u2x.3).
///
/// The grid is anchored on the view extent's origin — its left edge for x, and
/// for y the top edge under <c>upperLeft</c> (the spec default) or the bottom
/// edge under <c>lowerLeft</c> — so two requests over the same view agree on
/// the vertex values and a client can tile them. Snapping moves each ordinate
/// by at most half the tolerance; the follow-up
/// <see cref="GeometryGeneralization"/> spends the rest of the budget.
/// </summary>
internal sealed record EsriQuantization(double Tolerance, double OriginX, double OriginY)
{
    /// <summary>The modes the facade serves; anything else is rejected by name.</summary>
    private static readonly string[] Modes = ["view", "distance"];

    private static readonly string[] OriginPositions = ["upperLeft", "lowerLeft"];

    /// <summary>Parses the parameter, or null when it is absent or blank.</summary>
    public static EsriQuantization? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw GeoServicesErrors.Invalid($"'quantizationParameters' must be a JSON object, got an unparsable value: {exception.Message}.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw GeoServicesErrors.Invalid("'quantizationParameters' must be a JSON object.");
            }

            var root = document.RootElement;
            var mode = ReadString(root, "mode") ?? Modes[0];
            if (!Modes.Contains(mode, StringComparer.OrdinalIgnoreCase))
            {
                throw GeoServicesErrors.Invalid($"'quantizationParameters.mode' must be one of {string.Join(", ", Modes)}, got '{mode}'.");
            }

            var originPosition = ReadString(root, "originPosition") ?? OriginPositions[0];
            if (!OriginPositions.Contains(originPosition, StringComparer.OrdinalIgnoreCase))
            {
                throw GeoServicesErrors.Invalid($"'quantizationParameters.originPosition' must be one of {string.Join(", ", OriginPositions)}, got '{originPosition}'.");
            }

            var tolerance = ReadDouble(root, "tolerance")
                ?? throw GeoServicesErrors.Invalid("'quantizationParameters.tolerance' is required and must be a positive number.");
            if (!double.IsFinite(tolerance) || tolerance <= 0)
            {
                throw GeoServicesErrors.Invalid($"'quantizationParameters.tolerance' must be a positive number, got '{tolerance}'.");
            }

            var extent = ReadExtent(root);
            var upperLeft = originPosition.Equals(OriginPositions[0], StringComparison.OrdinalIgnoreCase);
            return new EsriQuantization(tolerance, extent.MinX, upperLeft ? extent.MaxY : extent.MinY);
        }
    }

    /// <summary>Snaps every ordinate of the geometry to the grid, keeping its kind and reference.</summary>
    public IGeometry Quantize(IGeometry geometry)
    {
        var single = QuantizeSingle(geometry);
        return single ?? QuantizeMulti(geometry) ?? geometry;
    }

    private IGeometry? QuantizeSingle(IGeometry geometry)
    {
        var crs = geometry.CoordinateReference;
        return geometry switch
        {
            Point point when point.Coordinate is { } coordinate =>
                GeometryFactory.CreatePoint(Snap(coordinate), crs),
            LineString line => QuantizeLine(line, crs),
            Polygon polygon => QuantizePolygon(polygon),
            _ => null,
        };
    }

    private IGeometry? QuantizeMulti(IGeometry geometry)
    {
        var crs = geometry.CoordinateReference;
        return geometry switch
        {
            MultiPoint multiPoint => GeometryFactory.CreateMultiPoint(multiPoint.Points.Select(point => GeometryFactory.CreatePoint(Snap(point.Coordinate ?? new Coordinate(0, 0)), crs)), crs),
            MultiLineString multiLine => GeometryFactory.CreateMultiLineString(multiLine.LineStrings.Select(line => QuantizeLine(line, crs)), crs),
            MultiPolygon multiPolygon => GeometryFactory.CreateMultiPolygon(multiPolygon.Polygons.Select(QuantizePolygon), crs),
            GeometryCollection collection => GeometryFactory.CreateGeometryCollection(collection.Geometries.Select(Quantize), crs),
            _ => null,
        };
    }

    private Polygon QuantizePolygon(Polygon polygon)
    {
        var crs = polygon.CoordinateReference;
        return GeometryFactory.CreatePolygon(
            QuantizeLine(polygon.ExteriorRing, crs),
            polygon.InteriorRings.Select(ring => QuantizeLine(ring, crs)),
            crs);
    }

    private LineString QuantizeLine(LineString line, CoordinateReference? crs)
    {
        var sequence = line.Sequence;
        var snapped = new Coordinate[sequence.Count];
        for (var i = 0; i < snapped.Length; i++)
        {
            snapped[i] = Snap(sequence.GetCoordinate(i));
        }

        return GeometryFactory.CreateLineString(snapped, sequence.Layout, crs ?? line.CoordinateReference);
    }

    /// <summary>Snaps one coordinate to the nearest grid intersection.</summary>
    private Coordinate Snap(Coordinate coordinate) => new(
        Snap(coordinate.X, OriginX),
        Snap(coordinate.Y, OriginY),
        SnapOrdinate(coordinate.Z),
        SnapOrdinate(coordinate.M));

    private double Snap(double value, double origin) => origin + (Math.Round((value - origin) / Tolerance, MidpointRounding.AwayFromZero) * Tolerance);

    /// <summary>
    /// Z and M quantize on the same value quantum as x and y, which is what
    /// makes the grid a distance grid rather than a per-axis decimal rounding.
    /// </summary>
    private double? SnapOrdinate(double? value) =>
        value is null || double.IsNaN(value.Value) ? value : Snap(value.Value, 0);

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    private static (double MinX, double MinY, double MaxX, double MaxY) ReadExtent(JsonElement root)
    {
        if (!root.TryGetProperty("extent", out var extent) || extent.ValueKind != JsonValueKind.Object)
        {
            throw GeoServicesErrors.Invalid("'quantizationParameters.extent' is required and must be an envelope object with 'xmin', 'ymin', 'xmax' and 'ymax'.");
        }

        var minX = ReadDouble(extent, "xmin");
        var minY = ReadDouble(extent, "ymin");
        var maxX = ReadDouble(extent, "xmax");
        var maxY = ReadDouble(extent, "ymax");
        var bounds = new[] { minX, minY, maxX, maxY };
        if (bounds.Any(bound => bound is not double number || !double.IsFinite(number))
            || minX >= maxX || minY >= maxY)
        {
            throw GeoServicesErrors.Invalid("'quantizationParameters.extent' must be a non-empty envelope of finite 'xmin', 'ymin', 'xmax' and 'ymax' numbers.");
        }

        return (minX!.Value, minY!.Value, maxX!.Value, maxY!.Value);
    }
}
