using System.Globalization;
using System.Text;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Tiling.Mvt;

/// <summary>
/// The Mapbox Vector Tile protobuf writer: the byte-level half of
/// <see cref="MvtTileService"/>, split out so the service owns only the
/// read/transform/queue side of a tile and this type owns geometry commands,
/// layers, features, keys, values and the varint framing.
/// </summary>
internal static class MvtTileWriter
{
    internal static byte[] WriteGeometry(IGeometry geometry, Envelope bounds, int extent)
    {
        using var output = new MemoryStream();
        var writer = new MvtGeometryWriter(output, bounds, extent);
        writer.Write(geometry);
        return output.ToArray();
    }

    private sealed class MvtGeometryWriter
    {
        internal static readonly Dictionary<GeometryType, Action<MvtGeometryWriter, IGeometry>> Writers =
            new Dictionary<GeometryType, Action<MvtGeometryWriter, IGeometry>>
            {
                [GeometryType.Point] = WritePoint,
                [GeometryType.MultiPoint] = WriteMultiPoint,
                [GeometryType.LineString] = WriteLine,
                [GeometryType.MultiLineString] = WriteMultiLine,
                [GeometryType.Polygon] = WritePolygon,
                [GeometryType.MultiPolygon] = WriteMultiPolygon,
            };

        private readonly Stream _output;
        private readonly Envelope _bounds;
        private readonly int _extent;
        private long _x;
        private long _y;

        public MvtGeometryWriter(Stream output, Envelope bounds, int extent)
        {
            _output = output;
            _bounds = bounds;
            _extent = extent;
        }

        public void Write(IGeometry geometry)
        {
            if (Writers.TryGetValue(geometry.Type, out var write))
            {
                write(this, geometry);
            }
        }

        private static void WritePoint(MvtGeometryWriter writer, IGeometry geometry) =>
            writer.WritePoint((IPoint)geometry);

        private static void WriteMultiPoint(MvtGeometryWriter writer, IGeometry geometry)
        {
            foreach (var point in ((IMultiPoint)geometry).Points)
            {
                writer.WritePoint(point);
            }
        }

        private static void WriteLine(MvtGeometryWriter writer, IGeometry geometry) =>
            writer.WriteLine((ILineString)geometry);

        private static void WriteMultiLine(MvtGeometryWriter writer, IGeometry geometry)
        {
            foreach (var line in ((IMultiLineString)geometry).LineStrings)
            {
                writer.WriteLine(line);
            }
        }

        private static void WritePolygon(MvtGeometryWriter writer, IGeometry geometry) =>
            writer.WritePolygon((IPolygon)geometry);

        private static void WriteMultiPolygon(MvtGeometryWriter writer, IGeometry geometry)
        {
            foreach (var polygon in ((IMultiPolygon)geometry).Polygons)
            {
                writer.WritePolygon(polygon);
            }
        }

        private void WritePoint(IPoint point)
        {
            if (point.Coordinate is { } coordinate)
            {
                MvtTileWriter.WritePoint(_output, coordinate, _bounds, _extent, ref _x, ref _y);
            }
        }

        private void WriteLine(ILineString line) =>
            MvtTileWriter.WriteLine(_output, line, _bounds, _extent, ref _x, ref _y);

        private void WritePolygon(IPolygon polygon) =>
            MvtTileWriter.WritePolygon(_output, polygon, _bounds, _extent, ref _x, ref _y);
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

        WritePath(output, line, bounds, extent, ref x, ref y);
    }

    private static void WriteLineCoordinates(Stream output, ILineString line, Envelope bounds, int extent, ref long x, ref long y)
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

    private static void WritePolygon(Stream output, IPolygon polygon, Envelope bounds, int extent, ref long x, ref long y)
    {
        WriteRing(output, polygon.ExteriorRing, bounds, extent, ref x, ref y);
        foreach (var ring in polygon.InteriorRings)
        {
            WriteRing(output, ring, bounds, extent, ref x, ref y);
        }
    }

    private static void WriteRing(Stream output, LineString ring, Envelope bounds, int extent, ref long x, ref long y)
    {
        if (ring.Sequence.Count == 0)
        {
            return;
        }

        WritePath(output, ring, bounds, extent, ref x, ref y);
        WriteCommand(output, 7, 1, 0, 0);
    }

    /// <summary>Moves to the path's first vertex, then writes its remaining coordinates; the path is non-empty.</summary>
    private static void WritePath(Stream output, ILineString path, Envelope bounds, int extent, ref long x, ref long y)
    {
        var (firstX, firstY) = Project(path[0], bounds, extent);
        WriteCommand(output, 1, 1, (int)(firstX - x), (int)(firstY - y));
        x = firstX;
        y = firstY;
        if (path.Sequence.Count > 1)
        {
            WriteLineCoordinates(output, path, bounds, extent, ref x, ref y);
        }
    }

    /// <summary>
    /// Places a coordinate in the tile's integer frame. The bounds must have
    /// extent on both axes: the tile is validated by <see cref="MvtTileService"/>
    /// before any layer is read, so a zero divisor here is a broken invariant
    /// rather than a caller's tile.
    /// </summary>
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

    internal static byte[] WriteTile(IReadOnlyList<MvtEncodedLayer> layers)
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

    private static byte[] WriteLayer(MvtEncodedLayer layer)
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

    private static byte[] WriteFeature(MvtEncodedFeature feature)
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

    /// <summary>
    /// The tile's value table entry: MVT 2.1 keeps the wire type in the key
    /// declaration, so the value is written as a string (field 1), a double
    /// (field 3), a varint (field 4) or a bool (field 7).
    /// </summary>
    private static byte[] WriteValue(AttributeValue value)
    {
        using var output = new MemoryStream();
        switch (value.Kind)
        {
            case AttributeKind.String:
            case AttributeKind.DateTimeOffset:
            case AttributeKind.Guid:
                WriteString(output, 1, TextOf(value));
                break;
            case AttributeKind.Double:
            case AttributeKind.Int64:
            case AttributeKind.Boolean:
                WriteScalar(output, value);
                break;
        }
        return output.ToArray();
    }

    private static string TextOf(AttributeValue value) => value.Kind switch
    {
        AttributeKind.DateTimeOffset => value.DateTimeOffsetValue.ToString("O", CultureInfo.InvariantCulture),
        AttributeKind.Guid => value.GuidValue.ToString(),
        _ => value.StringValue,
    };

    private static void WriteScalar(Stream output, AttributeValue value)
    {
        switch (value.Kind)
        {
            case AttributeKind.Double:
                WriteTag(output, 3, 1);
                WriteLittleEndian64(output, BitConverter.DoubleToInt64Bits(value.DoubleValue));
                break;
            case AttributeKind.Int64:
                WriteTag(output, 4, 0);
                WriteVarint(output, (ulong)value.Int64Value);
                break;
            default:
                WriteTag(output, 7, 0);
                WriteVarint(output, value.BooleanValue ? 1u : 0u);
                break;
        }
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
}
