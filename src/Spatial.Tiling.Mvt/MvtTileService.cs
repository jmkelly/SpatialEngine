using System.Globalization;
using System.Text;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Tiling.Mvt;

/// <summary>
/// MVT 2.1 vector-tile service. It reads resolved feature stores, projects
/// each geometry into the tile CRS, and writes the Mapbox Vector Tile
/// protobuf directly so no third-party encoding type enters the contracts.
/// </summary>
public sealed class MvtTileService : IVectorTileService
{
    private readonly ICoordinateTransforms _transforms;

    public MvtTileService(ICoordinateTransforms transforms) => _transforms = transforms;

    public async Task<VectorTile> RenderAsync(VectorTileRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Bounds.IsEmpty || string.IsNullOrWhiteSpace(request.Crs) || request.Extent is < 1 or > 65536)
        {
            throw SpatialException.BadArguments("A vector tile needs finite bounds, a CRS and an extent between 1 and 65536.");
        }

        var layers = new List<EncodedLayer>(request.Layers.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in request.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!names.Add(layer.Name))
            {
                throw SpatialException.BadArguments($"Vector tile layer name '{layer.Name}' is duplicated.");
            }
            layers.Add(await ReadLayerAsync(request, layer, cancellationToken).ConfigureAwait(false));
        }

        return new VectorTile(WriteTile(layers));
    }

    private async Task<EncodedLayer> ReadLayerAsync(
        VectorTileRequest request, VectorTileLayer layer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (string.IsNullOrWhiteSpace(layer.Name) || string.IsNullOrWhiteSpace(layer.Dataset))
        {
            throw SpatialException.BadArguments("Every vector tile layer needs a name and dataset.");
        }

        var description = await layer.Catalogue.DescribeAsync(layer.Dataset, cancellationToken).ConfigureAwait(false);
        var source = $"EPSG:{description.Srid}";
        var queryBounds = ProjectBounds(request.Bounds, request.Crs, source, cancellationToken);
        var batches = await layer.Features.QueryAsync(layer.Dataset, queryBounds, layer.Filter, cancellationToken).ConfigureAwait(false);
        var features = batches.SelectMany(batch => batch.Features).ToArray();
        var schema = features.FirstOrDefault()?.Schema ?? description.Schema;
        var geometryIndex = FindGeometry(schema);
        if (geometryIndex < 0)
        {
            throw SpatialException.BadArguments($"Vector tile layer '{layer.Name}' has no geometry field.");
        }
        var keys = schema.Fields.Where(field => field.Kind != AttributeKind.Geometry).ToArray();
        var values = new ValueTable();
        var encoded = new List<EncodedFeature>();
        foreach (var feature in features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (feature[geometryIndex].IsNull)
            {
                throw SpatialException.BadArguments($"Vector tile layer '{layer.Name}' contains a feature with no geometry.");
            }
            var geometry = _transforms.Transform(feature[geometryIndex].GeometryValue, source, request.Crs, cancellationToken);
            var type = GeometryTypeOf(geometry);
            if (type is null)
            {
                continue;
            }

            var tags = new List<uint>();
            for (var i = 0; i < feature.Attributes.Count; i++)
            {
                if (!feature[i].IsNull && feature[i].Kind != AttributeKind.Geometry)
                {
                    var key = Array.FindIndex(keys, field => field.Name == feature.Schema[i].Name);
                    if (key >= 0)
                    {
                        tags.Add((uint)key);
                        tags.Add(values.Index(feature[i]));
                    }
                }
            }

            encoded.Add(new EncodedFeature(ParseId(feature.Id), type.Value, tags.ToArray(), WriteGeometry(geometry, request.Bounds, request.Extent)));
        }

        return new EncodedLayer(layer.Name, keys, values, encoded, request.Extent);
    }

    private BoundingBox ProjectBounds(Envelope bounds, string target, string source, CancellationToken cancellationToken)
    {
        if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
        {
            return new BoundingBox(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
        }

        var separator = target.IndexOf(':');
        if (separator <= 0 || separator == target.Length - 1)
        {
            throw SpatialException.BadArguments($"Vector tile CRS '{target}' is not an authority:code identity.");
        }
        var ring = GeometryFactory.CreateLineString(
            [new Coordinate(bounds.MinX, bounds.MinY), new Coordinate(bounds.MaxX, bounds.MinY), new Coordinate(bounds.MaxX, bounds.MaxY), new Coordinate(bounds.MinX, bounds.MaxY), new Coordinate(bounds.MinX, bounds.MinY)],
            new CoordinateReference(target[..separator], target[(separator + 1)..]));
        var projected = _transforms.Transform(ring, target, source, cancellationToken).Envelope ?? bounds;
        return new BoundingBox(projected.MinX, projected.MinY, projected.MaxX, projected.MaxY);
    }

    private static int FindGeometry(FeatureSchema schema)
    {
        for (var i = 0; i < schema.Count; i++)
        {
            if (schema[i].Kind == AttributeKind.Geometry)
            {
                return i;
            }
        }
        return -1;
    }

    private static MvtGeometryType? GeometryTypeOf(IGeometry geometry) => geometry.Type switch
    {
        GeometryType.Point or GeometryType.MultiPoint => MvtGeometryType.Point,
        GeometryType.LineString or GeometryType.MultiLineString => MvtGeometryType.LineString,
        GeometryType.Polygon or GeometryType.MultiPolygon => MvtGeometryType.Polygon,
        _ => throw SpatialException.BadArguments($"MVT cannot encode a {geometry.Type} geometry; use a point, line or polygon collection."),
    };

    private static ulong? ParseId(FeatureId id) =>
        ulong.TryParse(id.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static byte[] WriteGeometry(IGeometry geometry, Envelope bounds, int extent)
    {
        using var output = new MemoryStream();
        var x = 0L;
        var y = 0L;
        switch (geometry)
        {
            case IPoint point when point.Coordinate is { } coordinate:
                WritePoint(output, coordinate, bounds, extent, ref x, ref y);
                break;
            case IMultiPoint multiPoint:
                foreach (var point in multiPoint.Points)
                {
                    if (point.Coordinate is { } coordinate)
                    {
                        WritePoint(output, coordinate, bounds, extent, ref x, ref y);
                    }
                }
                break;
            case ILineString line:
                WriteLine(output, line, bounds, extent, ref x, ref y);
                break;
            case IMultiLineString multiLine:
                foreach (var line in multiLine.LineStrings)
                {
                    WriteLine(output, line, bounds, extent, ref x, ref y);
                }
                break;
            case IPolygon polygon:
                WritePolygon(output, polygon, bounds, extent, ref x, ref y);
                break;
            case IMultiPolygon multiPolygon:
                foreach (var polygon in multiPolygon.Polygons)
                {
                    WritePolygon(output, polygon, bounds, extent, ref x, ref y);
                }
                break;
        }
        return output.ToArray();
    }

    private static void WritePoint(Stream output, Coordinate coordinate, Envelope bounds, int extent, ref long x, ref long y)
    {
        var (px, py) = Project(coordinate, bounds, extent);
        WriteCommand(output, 1, 1, (int)(px - x), (int)(py - y));
        x = px;
        y = py;
    }

    private static void WriteLine(Stream output, ILineString line, Envelope bounds, int extent, ref long x, ref long y)
    {
        if (line.Sequence.Count == 0)
        {
            return;
        }
        var (firstX, firstY) = Project(line[0], bounds, extent);
        WriteCommand(output, 1, 1, (int)(firstX - x), (int)(firstY - y));
        x = firstX;
        y = firstY;
        if (line.Sequence.Count > 1)
        {
            WriteCommandHeader(output, 2, line.Sequence.Count - 1);
            for (var i = 1; i < line.Sequence.Count; i++)
            {
                var (px, py) = Project(line[i], bounds, extent);
                WriteZigZag(output, (int)(px - x));
                WriteZigZag(output, (int)(py - y));
                x = px;
                y = py;
            }
        }
    }

    private static void WritePolygon(Stream output, IPolygon polygon, Envelope bounds, int extent, ref long x, ref long y)
    {
        WriteRing(output, polygon.ExteriorRing, bounds, extent, ref x, ref y, close: true);
        foreach (var ring in polygon.InteriorRings)
        {
            WriteRing(output, ring, bounds, extent, ref x, ref y, close: true);
        }
    }

    private static void WriteRing(Stream output, LineString ring, Envelope bounds, int extent, ref long x, ref long y, bool close)
    {
        if (ring.Sequence.Count == 0)
        {
            return;
        }
        var (firstX, firstY) = Project(ring[0], bounds, extent);
        WriteCommand(output, 1, 1, (int)(firstX - x), (int)(firstY - y));
        x = firstX;
        y = firstY;
        if (ring.Sequence.Count > 1)
        {
            WriteCommandHeader(output, 2, ring.Sequence.Count - 1);
            for (var i = 1; i < ring.Sequence.Count; i++)
            {
                var (px, py) = Project(ring[i], bounds, extent);
                WriteZigZag(output, (int)(px - x));
                WriteZigZag(output, (int)(py - y));
                x = px;
                y = py;
            }
        }
        if (close)
        {
            WriteCommand(output, 7, 1, 0, 0);
        }
    }

    private static (long X, long Y) Project(Coordinate coordinate, Envelope bounds, int extent)
    {
        var x = (coordinate.X - bounds.MinX) * extent / bounds.Width;
        var y = (bounds.MaxY - coordinate.Y) * extent / bounds.Height;
        return ((long)Math.Round(x, MidpointRounding.AwayFromZero), (long)Math.Round(y, MidpointRounding.AwayFromZero));
    }

    private static void WriteCommand(Stream output, int command, int count, int dx, int dy)
    {
        WriteCommandHeader(output, command, count);
        if (command != 7)
        {
            WriteZigZag(output, dx);
            WriteZigZag(output, dy);
        }
    }

    private static void WriteCommandHeader(Stream output, int command, int count) =>
        WriteVarint(output, (uint)((command & 7) | (count << 3)));

    private static byte[] WriteTile(IReadOnlyList<EncodedLayer> layers)
    {
        using var output = new MemoryStream();
        foreach (var layer in layers)
        {
            var bytes = WriteLayer(layer);
            WriteTag(output, 3, 2);
            WriteVarint(output, (uint)bytes.Length);
            output.Write(bytes);
        }
        return output.ToArray();
    }

    private static byte[] WriteLayer(EncodedLayer layer)
    {
        using var output = new MemoryStream();
        WriteString(output, 1, layer.Name);
        foreach (var feature in layer.Features)
        {
            var bytes = WriteFeature(feature);
            WriteTag(output, 2, 2);
            WriteVarint(output, (uint)bytes.Length);
            output.Write(bytes);
        }
        for (var i = 0; i < layer.Keys.Count; i++)
        {
            WriteString(output, 3, layer.Keys[i].Name);
        }
        for (var i = 0; i < layer.Values.Values.Count; i++)
        {
            var value = WriteValue(layer.Values.Values[i]);
            WriteTag(output, 4, 2);
            WriteVarint(output, (uint)value.Length);
            output.Write(value);
        }
        WriteTag(output, 5, 0);
        WriteVarint(output, (uint)layer.Extent);
        WriteTag(output, 15, 0);
        WriteVarint(output, 2);
        return output.ToArray();
    }

    private static byte[] WriteFeature(EncodedFeature feature)
    {
        using var output = new MemoryStream();
        if (feature.Id is { } id)
        {
            WriteTag(output, 1, 0);
            WriteVarint(output, id);
        }
        using (var tags = new MemoryStream())
        {
            foreach (var tag in feature.Tags)
            {
                WriteVarint(tags, tag);
            }
            var bytes = tags.ToArray();
            WriteTag(output, 2, 2);
            WriteVarint(output, (uint)bytes.Length);
            output.Write(bytes);
        }
        WriteTag(output, 3, 0);
        WriteVarint(output, (uint)feature.Type);
        WriteTag(output, 4, 2);
        WriteVarint(output, (uint)feature.Geometry.Length);
        output.Write(feature.Geometry);
        return output.ToArray();
    }

    private static byte[] WriteValue(AttributeValue value)
    {
        using var output = new MemoryStream();
        switch (value.Kind)
        {
            case AttributeKind.String:
                WriteString(output, 1, value.StringValue);
                break;
            case AttributeKind.Double:
                WriteTag(output, 3, 1);
                WriteLittleEndian64(output, BitConverter.DoubleToInt64Bits(value.DoubleValue));
                break;
            case AttributeKind.Int64:
                WriteTag(output, 4, 0);
                WriteVarint(output, (ulong)value.Int64Value);
                break;
            case AttributeKind.Boolean:
                WriteTag(output, 7, 0);
                WriteVarint(output, value.BooleanValue ? 1u : 0u);
                break;
            case AttributeKind.DateTimeOffset:
                WriteString(output, 1, value.DateTimeOffsetValue.ToString("O", CultureInfo.InvariantCulture));
                break;
            case AttributeKind.Guid:
                WriteString(output, 1, value.GuidValue.ToString());
                break;
        }
        return output.ToArray();
    }

    private static void WriteString(Stream output, int field, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteTag(output, field, 2);
        WriteVarint(output, (uint)bytes.Length);
        output.Write(bytes);
    }

    private static void WriteLittleEndian64(Stream output, long value)
    {
        Span<byte> bytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteTag(Stream output, int field, int wire)
    {
        var value = (uint)((field << 3) | wire);
        if (value < 0x80)
        {
            output.WriteByte((byte)value);
        }
        else
        {
            WriteVarint(output, value);
        }
    }

    private static void WriteVarint(Stream output, uint value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        output.WriteByte((byte)value);
    }

    private static void WriteVarint(Stream output, ulong value)
    {
        while (value >= 0x80)
        {
            output.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        output.WriteByte((byte)value);
    }

    private static void WriteZigZag(Stream output, int value) => WriteVarint(output, (ulong)((uint)((value << 1) ^ (value >> 31))));

    private sealed record EncodedLayer(string Name, IReadOnlyList<FieldDefinition> Keys, ValueTable Values, IReadOnlyList<EncodedFeature> Features, int Extent);
    private sealed record EncodedFeature(ulong? Id, MvtGeometryType Type, uint[] Tags, byte[] Geometry);

    private enum MvtGeometryType { Unknown = 0, Point = 1, LineString = 2, Polygon = 3 }

    private sealed class ValueTable
    {
        private readonly Dictionary<string, uint> _indexes = new(StringComparer.Ordinal);
        public List<AttributeValue> Values { get; } = [];

        public uint Index(AttributeValue value)
        {
            var key = $"{value.Kind}:{value}";
            if (_indexes.TryGetValue(key, out var index))
            {
                return index;
            }
            index = (uint)Values.Count;
            Values.Add(value);
            _indexes[key] = index;
            return index;
        }
    }
}
