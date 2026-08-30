using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Spatial.Core.Geometry;

/// <summary>
/// Canonical binary interchange for geometry values (ADR-0020). The format is
/// deterministic: structurally equal geometries encode to identical bytes,
/// and every geometry type round-trips exactly, including per-part layouts
/// and CRSs. All integers and doubles are little-endian.
///
/// <code>
/// Header:
///   byte[5]   magic "SGEOM"
///   byte      format version (1)
///
/// Node (recursive):
///   byte      layout (CoordinateLayout value)
///   byte      type (GeometryType value)
///   byte      hasCrs (0 or 1)
///   when hasCrs:
///     int32   authority UTF-8 byte length, bytes
///     int32   code UTF-8 byte length, bytes
///   body by type:
///     Point:             byte hasCoordinate (0 = empty, 1 = present); when present,
///                        layout stride × 8 bytes
///     LineString:        int32 count, count × stride doubles
///     Polygon:           int32 ring count, ring count line string nodes
///     MultiPoint / MultiLineString / MultiPolygon / GeometryCollection:
///                        int32 element count, element count child nodes
/// </code>
///
/// Nesting is bounded by <see cref="MaxNestingDepth"/> on both encode and
/// decode, and element counts are checked against the remaining input before
/// any allocation.
/// </summary>
public static class GeometryCodec
{
    /// <summary>Current canonical format version.</summary>
    public const byte FormatVersion = 1;

    /// <summary>Maximum geometry nesting depth accepted by encode and decode.</summary>
    public const int MaxNestingDepth = 256;

    /// <summary>Fixed header size: 5 magic bytes plus the version byte.</summary>
    public const int HeaderLength = 6;

    /// <summary>The canonical magic bytes "SGEOM".</summary>
    public static ReadOnlySpan<byte> Magic => "SGEOM"u8;

    /// <summary>Encodes a geometry to its canonical binary form.</summary>
    public static byte[] Encode(IGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        var totalLength = HeaderLength + ComputeNodeLength(geometry, 0);
        if (totalLength > int.MaxValue)
        {
            throw new ArgumentException($"Geometry is too large to encode ({totalLength} bytes).", nameof(geometry));
        }

        var buffer = new byte[(int)totalLength];
        var writer = new Writer(buffer);
        writer.WriteBytes(Magic);
        writer.WriteByte(FormatVersion);
        WriteNode(ref writer, geometry, 0);
        return buffer;
    }

    /// <summary>Decodes a canonical geometry, throwing <see cref="CanonicalFormatException"/> on invalid input.</summary>
    public static IGeometry Decode(ReadOnlySpan<byte> data)
    {
        if (!TryDecode(data, out var geometry, out var error))
        {
            throw new CanonicalFormatException(error);
        }

        return geometry!;
    }

    /// <summary>
    /// Decodes a canonical geometry. On failure, <paramref name="error"/>
    /// describes the problem with the offending byte offset; no exception is
    /// thrown.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IGeometry? geometry, [NotNullWhen(false)] out string? error)
    {
        geometry = null;

        if (data.Length < HeaderLength)
        {
            error = Error(0, $"header is truncated ({data.Length} of {HeaderLength} bytes).");
            return false;
        }

        if (!data.StartsWith(Magic))
        {
            error = "Invalid canonical geometry at byte offset 0: expected magic 'SGEOM'.";
            return false;
        }

        var version = data[HeaderLength - 1];
        if (version != FormatVersion)
        {
            error = Error(HeaderLength - 1, $"unsupported canonical geometry format version {version} (expected {FormatVersion}).");
            return false;
        }

        var reader = new Reader(data);
        reader.Advance(HeaderLength);

        if (!TryReadNode(ref reader, 0, out geometry, out error))
        {
            return false;
        }

        if (reader.Remaining != 0)
        {
            error = $"Invalid canonical geometry: {reader.Remaining} trailing bytes after the geometry payload at offset {reader.Position}.";
            geometry = null;
            return false;
        }

        return true;
    }

    private static long ComputeNodeLength(IGeometry geometry, int depth)
    {
        if (depth > MaxNestingDepth)
        {
            throw new ArgumentException($"Geometry nests deeper than the canonical format limit of {MaxNestingDepth} levels.", nameof(geometry));
        }

        var length = 2L + CrsLength(geometry.CoordinateReference);
        length += geometry switch
        {
            // The point body carries a coordinate-present flag plus the coordinate itself.
            Point point => 1 + (point.IsEmpty ? 0 : point.Layout.OrdinateCount() * 8L),
            LineString lineString => 4L + (long)lineString.Sequence.Count * lineString.Layout.OrdinateCount() * 8,
            Polygon polygon => 4L + ComputeNodeLength(polygon.ExteriorRing, depth + 1) + polygon.InteriorRings.Sum(ring => ComputeNodeLength(ring, depth + 1)),
            MultiPoint multiPoint => 4L + multiPoint.Points.Sum(part => ComputeNodeLength(part, depth + 1)),
            MultiLineString multiLineString => 4L + multiLineString.LineStrings.Sum(part => ComputeNodeLength(part, depth + 1)),
            MultiPolygon multiPolygon => 4L + multiPolygon.Polygons.Sum(part => ComputeNodeLength(part, depth + 1)),
            GeometryCollection collection => 4L + collection.Geometries.Sum(part => ComputeNodeLength(part, depth + 1)),
            _ => throw new ArgumentException($"Unknown geometry type '{geometry.Type}'.", nameof(geometry)),
        };
        return length;
    }

    private static int CrsLength(CoordinateReference? crs) => crs is { } value
        ? 1 + 4 + Encoding.UTF8.GetByteCount(value.Authority) + 4 + Encoding.UTF8.GetByteCount(value.Code)
        : 1;

    private static void WriteNode(ref Writer writer, IGeometry geometry, int depth)
    {
        if (depth > MaxNestingDepth)
        {
            throw new ArgumentException($"Geometry nests deeper than the canonical format limit of {MaxNestingDepth} levels.", nameof(geometry));
        }

        writer.WriteByte((byte)geometry.Layout);
        writer.WriteByte((byte)geometry.Type);
        WriteCrs(ref writer, geometry.CoordinateReference);

        switch (geometry)
        {
            case Point point:
                writer.WriteByte(point.IsEmpty ? (byte)0 : (byte)1);
                if (point.Coordinate is { } coordinate)
                {
                    WriteCoordinate(ref writer, coordinate, point.Layout);
                }

                break;

            case LineString lineString:
                WriteSequence(ref writer, lineString);
                break;

            case Polygon polygon:
                writer.WriteInt32(1 + polygon.InteriorRings.Count);
                WriteNode(ref writer, polygon.ExteriorRing, depth + 1);
                foreach (var ring in polygon.InteriorRings)
                {
                    WriteNode(ref writer, ring, depth + 1);
                }

                break;

            case MultiPoint multiPoint:
                writer.WriteInt32(multiPoint.Points.Count);
                foreach (var part in multiPoint.Points)
                {
                    WriteNode(ref writer, part, depth + 1);
                }

                break;

            case MultiLineString multiLineString:
                writer.WriteInt32(multiLineString.LineStrings.Count);
                foreach (var part in multiLineString.LineStrings)
                {
                    WriteNode(ref writer, part, depth + 1);
                }

                break;

            case MultiPolygon multiPolygon:
                writer.WriteInt32(multiPolygon.Polygons.Count);
                foreach (var part in multiPolygon.Polygons)
                {
                    WriteNode(ref writer, part, depth + 1);
                }

                break;

            case GeometryCollection collection:
                writer.WriteInt32(collection.Geometries.Count);
                foreach (var part in collection.Geometries)
                {
                    WriteNode(ref writer, part, depth + 1);
                }

                break;

            default:
                throw new ArgumentException($"Unknown geometry type '{geometry.Type}'.", nameof(geometry));
        }
    }

    private static void WriteSequence(ref Writer writer, LineString lineString)
    {
        var sequence = lineString.Sequence;
        writer.WriteInt32(sequence.Count);
        for (var i = 0; i < sequence.Count; i++)
        {
            WriteCoordinate(ref writer, sequence.GetCoordinate(i), sequence.Layout);
        }
    }

    private static void WriteCoordinate(ref Writer writer, Coordinate coordinate, CoordinateLayout layout)
    {
        writer.WriteDouble(coordinate.X);
        writer.WriteDouble(coordinate.Y);
        if (layout.HasZ())
        {
            writer.WriteDouble(coordinate.Z ?? double.NaN);
        }

        if (layout.HasM())
        {
            writer.WriteDouble(coordinate.M ?? double.NaN);
        }
    }

    private static void WriteCrs(ref Writer writer, CoordinateReference? crs)
    {
        if (crs is not { } value)
        {
            writer.WriteByte(0);
            return;
        }

        writer.WriteByte(1);
        writer.WriteString(value.Authority);
        writer.WriteString(value.Code);
    }

    private static bool TryReadNode(ref Reader reader, int depth, [NotNullWhen(true)] out IGeometry? geometry, [NotNullWhen(false)] out string? error)
    {
        geometry = null;

        if (depth > MaxNestingDepth)
        {
            error = $"Geometry nesting exceeds the canonical format limit of {MaxNestingDepth} levels.";
            return false;
        }

        var offset = reader.Position;
        if (!reader.TryReadByte(out var layoutByte))
        {
            error = Error(offset, "expected a coordinate layout byte; input is truncated.");
            return false;
        }

        if (!Enum.IsDefined(typeof(CoordinateLayout), (CoordinateLayout)layoutByte))
        {
            error = Error(offset, $"unknown coordinate layout byte {layoutByte}.");
            return false;
        }

        if (!reader.TryReadByte(out var typeByte))
        {
            error = Error(offset, "expected a geometry type byte; input is truncated.");
            return false;
        }

        if (!Enum.IsDefined(typeof(GeometryType), (GeometryType)typeByte))
        {
            error = Error(offset, $"unknown geometry type byte {typeByte}.");
            return false;
        }

        var layout = (CoordinateLayout)layoutByte;
        if (!TryReadCrs(ref reader, out var crs, out error))
        {
            return false;
        }

        return (GeometryType)typeByte switch
        {
            GeometryType.Point => TryReadPoint(ref reader, layout, crs, out geometry, out error),
            GeometryType.LineString => TryReadLineString(ref reader, layout, crs, out geometry, out error),
            GeometryType.Polygon => TryReadPolygon(ref reader, layout, crs, depth, out geometry, out error),
            GeometryType.MultiPoint => TryReadMultiPoint(ref reader, layout, crs, depth, out geometry, out error),
            GeometryType.MultiLineString => TryReadMultiLineString(ref reader, layout, crs, depth, out geometry, out error),
            GeometryType.MultiPolygon => TryReadMultiPolygon(ref reader, layout, crs, depth, out geometry, out error),
            GeometryType.GeometryCollection => TryReadGeometryCollection(ref reader, layout, crs, depth, out geometry, out error),
            _ => Fail(offset, $"geometry type byte {typeByte} is not supported.", out geometry, out error),
        };
    }

    private static bool TryReadCrs(ref Reader reader, out CoordinateReference? crs, [NotNullWhen(false)] out string? error)
    {
        crs = null;
        var offset = reader.Position;

        if (!reader.TryReadByte(out var hasCrs))
        {
            error = Error(offset, "expected a CRS presence byte; input is truncated.");
            return false;
        }

        if (hasCrs > 1)
        {
            error = Error(offset, $"invalid CRS presence byte {hasCrs} (expected 0 or 1).");
            return false;
        }

        if (hasCrs == 0)
        {
            error = null;
            return true;
        }

        if (!reader.TryReadString(out var authority))
        {
            error = Error(offset, "expected a CRS authority string; input is truncated or not valid UTF-8.");
            return false;
        }

        if (!reader.TryReadString(out var code))
        {
            error = Error(offset, "expected a CRS code string; input is truncated or not valid UTF-8.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(code))
        {
            error = Error(offset, "invalid CRS: authority and code must be non-empty strings.");
            return false;
        }

        crs = new CoordinateReference(authority, code);
        error = null;
        return true;
    }

    private static bool TryReadPoint(ref Reader reader, CoordinateLayout layout, CoordinateReference? crs, out IGeometry? geometry, out string? error)
    {
        geometry = null;
        var offset = reader.Position;

        if (!reader.TryReadByte(out var hasCoordinate))
        {
            error = Error(offset, "expected a point coordinate presence byte; input is truncated.");
            return false;
        }

        if (hasCoordinate > 1)
        {
            error = Error(offset, $"invalid point coordinate presence byte {hasCoordinate} (expected 0 or 1).");
            return false;
        }

        if (hasCoordinate == 0)
        {
            geometry = new Point(null, crs, layout);
            error = null;
            return true;
        }

        var stride = layout.OrdinateCount();
        var requiredBytes = stride * 8;
        if (reader.Remaining < requiredBytes)
        {
            error = Error(offset, $"point body is truncated: {reader.Remaining} bytes remain but {requiredBytes} are required for layout {layout}.");
            return false;
        }

        var hasZ = layout.HasZ();
        var hasM = layout.HasM();
        double z = 0, m = 0;
        if (!reader.TryReadDouble(out var x)
            || !reader.TryReadDouble(out var y)
            || (hasZ && !reader.TryReadDouble(out z))
            || (hasM && !reader.TryReadDouble(out m)))
        {
            error = Error(offset, $"point body is truncated for layout {layout}.");
            return false;
        }

        geometry = new Point(new Coordinate(x, y, hasZ ? z : null, hasM ? m : null), crs, layout);
        error = null;
        return true;
    }

    private static bool TryReadLineString(ref Reader reader, CoordinateLayout layout, CoordinateReference? crs, out IGeometry? geometry, out string? error)
    {
        geometry = null;
        var offset = reader.Position;

        if (!TryReadElementCount(ref reader, "line string", out var count, out error))
        {
            return false;
        }

        var stride = layout.OrdinateCount();
        if ((long)count * stride * 8 > reader.Remaining)
        {
            error = Error(offset, $"line string is truncated: declares {count} coordinates but only {reader.Remaining} bytes remain ({count * stride * 8L} required).");
            return false;
        }

        var values = new double[count * stride];
        if (!TryReadDoubles(ref reader, values, out error))
        {
            error = Error(offset, $"line string body: {error}");
            return false;
        }

        geometry = new LineString(new PackedCoordinateSequence(values, layout), crs);
        error = null;
        return true;
    }

    private static bool TryReadPolygon(ref Reader reader, CoordinateLayout layout, CoordinateReference? crs, int depth, out IGeometry? geometry, out string? error)
    {
        geometry = null;
        var offset = reader.Position;

        if (!TryReadElementCount(ref reader, "polygon", out var ringCount, out error))
        {
            return false;
        }

        if (ringCount == 0)
        {
            geometry = new Polygon(EmptyRing(layout, crs), coordinateReference: crs);
            error = null;
            return true;
        }

        var rings = new List<LineString>(ringCount);
        for (var i = 0; i < ringCount; i++)
        {
            if (!TryReadNode(ref reader, depth + 1, out var ring, out error))
            {
                error = $"ring {i}: {error}";
                return false;
            }

            if (ring is not LineString lineString)
            {
                error = Error(offset, $"ring {i} is a {ring!.Type}, expected a line string.");
                return false;
            }

            rings.Add(lineString);
        }

        geometry = new Polygon(rings[0], rings.Skip(1).ToArray(), crs);
        error = null;
        return true;
    }

    private static bool TryReadMultiPoint(ref Reader reader, CoordinateLayout layout, CoordinateReference? crs, int depth, out IGeometry? geometry, out string? error)
    {
        geometry = null;
        if (!TryReadElementCount(ref reader, "multi-point", out var count, out error))
        {
            return false;
        }

        if (!TryReadChildNodes(ref reader, count, GeometryType.Point, "point", depth, out var children, out error))
        {
            return false;
        }

        geometry = new MultiPoint(children.Cast<Point>(), crs);
        error = null;
        return true;
    }

    private static bool TryReadMultiLineString(ref Reader reader, CoordinateLayout layout, CoordinateReference? crs, int depth, out IGeometry? geometry, out string? error)
    {
        geometry = null;
        if (!TryReadElementCount(ref reader, "multi-line string", out var count, out error))
        {
            return false;
        }

        if (!TryReadChildNodes(ref reader, count, GeometryType.LineString, "line string", depth, out var children, out error))
        {
            return false;
        }

        geometry = new MultiLineString(children.Cast<LineString>(), crs);
        error = null;
        return true;
    }

    private static bool TryReadMultiPolygon(ref Reader reader, CoordinateLayout layout, CoordinateReference? crs, int depth, out IGeometry? geometry, out string? error)
    {
        geometry = null;
        if (!TryReadElementCount(ref reader, "multi-polygon", out var count, out error))
        {
            return false;
        }

        if (!TryReadChildNodes(ref reader, count, GeometryType.Polygon, "polygon", depth, out var children, out error))
        {
            return false;
        }

        geometry = new MultiPolygon(children.Cast<Polygon>(), crs);
        error = null;
        return true;
    }

    private static bool TryReadGeometryCollection(ref Reader reader, CoordinateLayout layout, CoordinateReference? crs, int depth, out IGeometry? geometry, out string? error)
    {
        geometry = null;
        if (!TryReadElementCount(ref reader, "geometry collection", out var count, out error))
        {
            return false;
        }

        if (!TryReadChildNodes(ref reader, count, GeometryType.GeometryCollection, "part", depth, out var children, out error))
        {
            return false;
        }

        geometry = new GeometryCollection(children, crs);
        error = null;
        return true;
    }

    private static bool TryReadChildNodes(ref Reader reader, int count, GeometryType expectedType, string elementName, int depth, out List<IGeometry> children, out string? error)
    {
        children = new List<IGeometry>(count);
        for (var i = 0; i < count; i++)
        {
            if (!TryReadNode(ref reader, depth + 1, out var child, out error))
            {
                error = $"{elementName} {i}: {error}";
                return false;
            }

            if (expectedType != GeometryType.GeometryCollection && child!.Type != expectedType)
            {
                error = $"{elementName} {i} is a {child.Type}, expected {expectedType}.";
                return false;
            }

            children.Add(child);
        }

        error = null;
        return true;
    }

    private static bool TryReadElementCount(ref Reader reader, string element, out int count, out string? error)
    {
        error = null;
        count = 0;
        var offset = reader.Position;

        if (!reader.TryReadInt32(out count) || count < 0)
        {
            error = Error(offset, $"invalid {element} element count (must be non-negative).");
            return false;
        }

        if ((long)count * 3 > reader.Remaining)
        {
            error = Error(offset, $"{element} declares {count} elements but only {reader.Remaining} bytes remain (each element needs at least 3 bytes).");
            return false;
        }

        return true;
    }

    private static bool TryReadDoubles(ref Reader reader, Span<double> values, out string? error)
    {
        foreach (ref var value in values)
        {
            if (!reader.TryReadDouble(out value))
            {
                error = $"unexpected end of input at byte offset {reader.Position} while reading {values.Length} doubles.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static LineString EmptyRing(CoordinateLayout layout, CoordinateReference? crs) =>
        new(PackedCoordinateSequence.FromCoordinates([], layout), crs);

    private static bool Fail(int offset, string detail, out IGeometry? geometry, out string? error)
    {
        geometry = null;
        error = Error(offset, detail);
        return false;
    }

    private static string Error(int offset, string detail) =>
        FormattableString.Invariant($"Invalid canonical geometry at byte offset {offset}: {detail}");

    private ref struct Reader
    {
        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public Reader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public int Position => _position;

        public int Remaining => _data.Length - _position;

        public void Advance(int count) => _position += count;

        public bool TryReadByte(out byte value)
        {
            if (_position >= _data.Length)
            {
                value = 0;
                return false;
            }

            value = _data[_position++];
            return true;
        }

        public bool TryReadInt32(out int value)
        {
            if (!BinaryPrimitives.TryReadInt32LittleEndian(_data[_position..], out value))
            {
                return false;
            }

            _position += 4;
            return true;
        }

        public bool TryReadDouble(out double value)
        {
            if (!BinaryPrimitives.TryReadDoubleLittleEndian(_data[_position..], out value))
            {
                return false;
            }

            _position += 8;
            return true;
        }

        public bool TryReadString(out string value)
        {
            value = string.Empty;
            if (!TryReadInt32(out var length) || length < 0 || length > Remaining)
            {
                return false;
            }

            try
            {
                value = StrictUtf8.GetString(_data.Slice(_position, length));
            }
            catch (DecoderFallbackException)
            {
                return false;
            }

            _position += length;
            return true;
        }
    }

    private ref struct Writer
    {
        private readonly Span<byte> _buffer;
        private int _position;

        public Writer(Span<byte> buffer)
        {
            _buffer = buffer;
            _position = 0;
        }

        public void WriteByte(byte value) => _buffer[_position++] = value;

        public void WriteInt32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer[_position..], value);
            _position += 4;
        }

        public void WriteDouble(double value)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(_buffer[_position..], value);
            _position += 8;
        }

        public void WriteString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteInt32(bytes.Length);
            bytes.CopyTo(_buffer[_position..]);
            _position += bytes.Length;
        }

        public void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(_buffer[_position..]);
            _position += bytes.Length;
        }
    }
}
