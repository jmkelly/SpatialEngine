using System.Buffers.Binary;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Provider.PostGIS.Geometry;

/// <summary>
/// The plugin's **single** <c>Spatial.Core.Geometry</c> surface (ADR-0028
/// fan-in budget): the PostGIS EWKB ↔ core-geometry interchange. Everything
/// else in the provider passes geometry around as <see cref="AttributeValue"/>
/// (an EWKB payload on read, an attribute on write) and never names a core
/// geometry type. The interchange is byte-deterministic (all output is
/// little-endian NDR), handles the PostGIS SRID flag
/// (<c>0x20000000</c>), Z (<c>0x80000000</c>) and M (<c>0x40000000</c>)
/// flags, per-child byte order, NaN-sentinel empty points, and stamps CRS
/// identities (<c>EPSG:&lt;srid&gt;</c>, null for SRID 0) on the top-level
/// geometry. Malformed stored bytes fail with byte-accurate
/// <see cref="PostgisEwkbFormatException"/>; a write whose geometry CRS
/// conflicts with the dataset column's SRID fails with
/// <see cref="PostgisCrsMismatchException"/> — both mapped by the runners to
/// structured capability errors.
/// </summary>
internal static class PostgisEwkb
{
    private const uint ZFlag = 0x80000000;
    private const uint MFlag = 0x40000000;
    private const uint SridFlag = 0x20000000;
    private const uint BboxFlag = 0x10000000;
    private const uint TypeMask = 0xFF;

    /// <summary>The little-endian byte-order marker EWKB always uses in PostGIS output.</summary>
    private const byte Ndr = 1;

    /// <summary>Decodes a stored geometry into an attribute value (EWKB → core geometry → attribute).</summary>
    public static AttributeValue ReadGeometry(byte[] ewkb)
    {
        ArgumentNullException.ThrowIfNull(ewkb);
        return AttributeValue.FromGeometry(Decode(ewkb, srid => CoordinateReference.Epsg(srid)));
    }

    /// <summary>
    /// Encodes a geometry attribute for storage against a dataset column at
    /// <paramref name="datasetSrid"/>. When the geometry carries a CRS it must
    /// agree with the column (a conflict is <see cref="PostgisCrsMismatchException"/>);
    /// a CRS-less geometry is stored at the column's SRID.
    /// </summary>
    public static byte[] WriteGeometry(AttributeValue value, int datasetSrid)
    {
        var geometry = value.GeometryValue;
        if (geometry.CoordinateReference is { } stamped && !Matches(stamped, datasetSrid))
        {
            throw new PostgisCrsMismatchException(
                $"the geometry carries {stamped} but the dataset column stores SRID {datasetSrid}; transform it first or write to a matching column.");
        }

        return Encode(geometry, datasetSrid);
    }

    private static bool Matches(CoordinateReference reference, int srid) =>
        reference.Authority.Equals("EPSG", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(reference.Code, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var code)
        && code == srid;

    // ---- Reading (EWKB bytes → IGeometry) ----

    private static IGeometry Decode(byte[] bytes, Func<int, CoordinateReference?> crs)
    {
        var cursor = new Reader(bytes);
        return ReadGeometry(cursor, crs);
    }

    private static IGeometry ReadGeometry(Reader cursor, Func<int, CoordinateReference?> crs)
    {
        var endian = cursor.ReadByte();
        var type = cursor.ReadUInt32(endian);
        if ((type & BboxFlag) != 0)
        {
            throw Format(cursor, "bbox-prefixed EWKB is not supported");
        }

        var srid = 0;
        CoordinateReference? reference = null;
        if ((type & SridFlag) != 0)
        {
            srid = checked((int)cursor.ReadUInt32(endian));
            reference = crs(srid);
        }

        var flags = Of(type);
        if (!GeometryReaders.TryGetValue((GeometryType)(type & TypeMask), out var read))
        {
            throw Format(cursor, $"EWKB geometry type {(type & TypeMask)} is not supported");
        }

        return read(cursor, endian, flags, reference);
    }

    /// <summary>
    /// Table-driven body dispatch: the concrete geometry type selects the
    /// small typed reader, so the dispatcher carries no per-case branch and a
    /// new core geometry type needs exactly one entry here.
    /// </summary>
    private static readonly Dictionary<GeometryType, Func<Reader, byte, LayoutFlags, CoordinateReference?, IGeometry>> GeometryReaders = new()
    {
        [GeometryType.Point] = (cursor, endian, flags, crs) => ReadPoint(cursor, endian, flags, crs),
        [GeometryType.LineString] = (cursor, endian, flags, crs) => ReadLineString(cursor, endian, flags, crs),
        [GeometryType.Polygon] = (cursor, endian, flags, crs) => ReadPolygon(cursor, endian, flags, crs),
        [GeometryType.MultiPoint] = (cursor, endian, flags, crs) => ReadMultiPoint(cursor, endian, flags, crs),
        [GeometryType.MultiLineString] = (cursor, endian, flags, crs) => ReadMultiLineString(cursor, endian, flags, crs),
        [GeometryType.MultiPolygon] = (cursor, endian, flags, crs) => ReadMultiPolygon(cursor, endian, flags, crs),
        [GeometryType.GeometryCollection] = (cursor, endian, flags, crs) => ReadCollection(cursor, endian, flags, crs),
    };

    private static Point ReadPoint(Reader cursor, byte endian, LayoutFlags flags, CoordinateReference? crs)
    {
        var x = cursor.ReadDouble(endian);
        var y = cursor.ReadDouble(endian);
        if (double.IsNaN(x) && double.IsNaN(y))
        {
            return GeometryFactory.CreateEmptyPoint(crs, LayoutOf(flags));
        }

        return CreatePoint(cursor, endian, flags, x, y, crs);
    }

    /// <summary>The ordinate-aware point builders, keyed by the present Z/M flags (one read per present ordinate).</summary>
    private static readonly Dictionary<(bool HasZ, bool HasM), Func<double, double, double, double, CoordinateReference?, Point>> PointBuilders = new()
    {
        [(false, false)] = (x, y, _, _, crs) => GeometryFactory.CreatePoint(x, y, crs),
        [(true, false)] = (x, y, z, _, crs) => GeometryFactory.CreatePoint(x, y, z, crs),
        [(false, true)] = (x, y, _, m, crs) => GeometryFactory.CreatePoint(new Coordinate(x, y, M: m), crs),
        [(true, true)] = (x, y, z, m, crs) => GeometryFactory.CreatePoint(x, y, z, m, crs),
    };

    /// <summary>Reads the ordinates the flags declare and builds the matching point layout.</summary>
    private static Point CreatePoint(Reader cursor, byte endian, LayoutFlags flags, double x, double y, CoordinateReference? crs)
    {
        var z = flags.HasZ ? cursor.ReadDouble(endian) : double.NaN;
        var m = flags.HasM ? cursor.ReadDouble(endian) : double.NaN;
        return PointBuilders[(flags.HasZ, flags.HasM)](x, y, z, m, crs);
    }

    private static LineString ReadLineString(Reader cursor, byte endian, LayoutFlags flags, CoordinateReference? crs)
    {
        var count = cursor.ReadCount(endian, "line string coordinate count");
        return count == 0
            ? GeometryFactory.CreateEmptyLineString(LayoutOf(flags), crs)
            : GeometryFactory.CreateLineString(ReadCoordinates(cursor, endian, flags, count), LayoutOf(flags), crs);
    }

    private static Polygon ReadPolygon(Reader cursor, byte endian, LayoutFlags flags, CoordinateReference? crs)
    {
        var ringCount = cursor.ReadCount(endian, "polygon ring count");
        if (ringCount == 0)
        {
            return new Polygon(GeometryFactory.CreateEmptyLineString(LayoutOf(flags)), null, crs);
        }

        var rings = new LineString[ringCount];
        for (var i = 0; i < ringCount; i++)
        {
            var pointCount = cursor.ReadCount(endian, "polygon ring point count");
            rings[i] = new LineString(PackedCoordinateSequence.FromCoordinates(ReadCoordinates(cursor, endian, flags, pointCount), LayoutOf(flags)));
        }

        return GeometryFactory.CreatePolygon(rings[0], rings.Skip(1), crs);
    }

    private static MultiPoint ReadMultiPoint(Reader cursor, byte endian, LayoutFlags flags, CoordinateReference? crs)
    {
        var count = cursor.ReadCount(endian, "multipoint count");
        var points = new Point[count];
        for (var i = 0; i < count; i++)
        {
            points[i] = ReadAsType<Point>(cursor, GeometryType.Point, crs);
        }

        return GeometryFactory.CreateMultiPoint(points, crs);
    }

    private static MultiLineString ReadMultiLineString(Reader cursor, byte endian, LayoutFlags flags, CoordinateReference? crs)
    {
        var count = cursor.ReadCount(endian, "multilinestring count");
        var lines = new LineString[count];
        for (var i = 0; i < count; i++)
        {
            lines[i] = ReadAsType<LineString>(cursor, GeometryType.LineString, crs);
        }

        return GeometryFactory.CreateMultiLineString(lines, crs);
    }

    private static MultiPolygon ReadMultiPolygon(Reader cursor, byte endian, LayoutFlags flags, CoordinateReference? crs)
    {
        var count = cursor.ReadCount(endian, "multipolygon count");
        var polygons = new Polygon[count];
        for (var i = 0; i < count; i++)
        {
            polygons[i] = ReadAsType<Polygon>(cursor, GeometryType.Polygon, crs);
        }

        return GeometryFactory.CreateMultiPolygon(polygons, crs);
    }

    private static GeometryCollection ReadCollection(Reader cursor, byte endian, LayoutFlags flags, CoordinateReference? crs)
    {
        var count = cursor.ReadCount(endian, "collection count");
        var parts = new IGeometry[count];
        for (var i = 0; i < count; i++)
        {
            parts[i] = ReadGeometry(cursor, srid => null);
        }

        return GeometryFactory.CreateGeometryCollection(parts, crs);
    }

    /// <summary>Reads a child geometry and requires it to be exactly the expected type.</summary>
    private static T ReadAsType<T>(Reader cursor, GeometryType expected, CoordinateReference? crs)
        where T : IGeometry
    {
        var part = ReadGeometry(cursor, srid => null);
        if (part.Type != expected)
        {
            throw Format(cursor, $"expected a {expected} child, found {part.Type}");
        }

        return (T)part;
    }

    private static Coordinate[] ReadCoordinates(Reader cursor, byte endian, LayoutFlags flags, int count)
    {
        var coordinates = new Coordinate[count];
        for (var i = 0; i < count; i++)
        {
            var x = cursor.ReadDouble(endian);
            var y = cursor.ReadDouble(endian);
            double? z = flags.HasZ ? cursor.ReadDouble(endian) : null;
            double? m = flags.HasM ? cursor.ReadDouble(endian) : null;
            coordinates[i] = new Coordinate(x, y, z, m);
        }

        return coordinates;
    }

    // ---- Writing (IGeometry → EWKB bytes) ----

    private static byte[] Encode(IGeometry geometry, int srid)
    {
        var writer = new Writer();
        WriteGeometry(writer, geometry, srid);
        return writer.ToArray();
    }

    private static void WriteGeometry(Writer writer, IGeometry geometry, int srid)
    {
        WriteHeader(writer, geometry, srid);
        WriteBody(writer, geometry);
    }

    /// <summary>Writes the NDR byte-order marker and the type word (layout flags plus the SRID flag/word when stamped).</summary>
    private static void WriteHeader(Writer writer, IGeometry geometry, int srid)
    {
        writer.WriteByte(Ndr);
        var type = (uint)geometry.Type;
        var layout = geometry.Layout;
        if (layout.HasZ())
        {
            type |= ZFlag;
        }

        if (layout.HasM())
        {
            type |= MFlag;
        }

        if (srid > 0)
        {
            type |= SridFlag;
            writer.WriteUInt32(type);
            writer.WriteInt32(srid);
        }
        else
        {
            writer.WriteUInt32(type);
        }
    }

    /// <summary>
    /// Table-driven body dispatch: the concrete geometry type selects the
    /// small typed writer, so the dispatcher carries no per-case branch and a
    /// new core geometry type needs exactly one entry here.
    /// </summary>
    private static readonly Dictionary<GeometryType, Action<Writer, IGeometry>> GeometryWriters = new()
    {
        [GeometryType.Point] = (writer, geometry) => WritePoint(writer, (IPoint)geometry),
        [GeometryType.LineString] = (writer, geometry) => WriteLineString(writer, (ILineString)geometry),
        [GeometryType.Polygon] = (writer, geometry) => WritePolygon(writer, (IPolygon)geometry),
        [GeometryType.MultiPoint] = (writer, geometry) => WriteMulti(writer, ((IMultiPoint)geometry).Points),
        [GeometryType.MultiLineString] = (writer, geometry) => WriteMulti(writer, ((IMultiLineString)geometry).LineStrings),
        [GeometryType.MultiPolygon] = (writer, geometry) => WriteMulti(writer, ((IMultiPolygon)geometry).Polygons),
        [GeometryType.GeometryCollection] = (writer, geometry) => WriteMulti(writer, ((IGeometryParts)geometry).Geometries),
    };

    private static void WriteBody(Writer writer, IGeometry geometry)
    {
        if (GeometryWriters.TryGetValue(geometry.Type, out var write))
        {
            write(writer, geometry);
            return;
        }

        throw new NotSupportedException($"cannot write EWKB for geometry type {geometry.Type}");
    }

    private static void WritePoint(Writer writer, IPoint point)
    {
        var layout = point.Layout;
        if (point.Coordinate is not { } coordinate)
        {
            WriteOrdinates(writer, double.NaN, double.NaN);
            return;
        }

        if (layout.HasZ() && layout.HasM())
        {
            WriteOrdinates(writer, coordinate.X, coordinate.Y, coordinate.Z ?? double.NaN, coordinate.M ?? double.NaN);
        }
        else if (layout.HasZ())
        {
            WriteOrdinates(writer, coordinate.X, coordinate.Y, coordinate.Z ?? double.NaN);
        }
        else if (layout.HasM())
        {
            WriteOrdinates(writer, coordinate.X, coordinate.Y, coordinate.M ?? double.NaN);
        }
        else
        {
            WriteOrdinates(writer, coordinate.X, coordinate.Y);
        }
    }

    private static void WriteLineString(Writer writer, ILineString line) => WriteSequence(writer, line.Sequence);

    private static void WritePolygon(Writer writer, IPolygon polygon)
    {
        if (polygon.IsEmpty)
        {
            WriteCount(writer, 0);
            return;
        }

        WriteCount(writer, polygon.InteriorRings.Count + 1);
        WriteSequence(writer, polygon.ExteriorRing.Sequence);
        foreach (var ring in polygon.InteriorRings)
        {
            WriteSequence(writer, ring.Sequence);
        }
    }

    private static void WriteMulti<T>(Writer writer, IReadOnlyList<T> parts)
        where T : IGeometry
    {
        WriteCount(writer, parts.Count);
        foreach (var part in parts)
        {
            WriteGeometry(writer, part, srid: 0);
        }
    }

    private static void WriteSequence(Writer writer, ICoordinateSequence sequence)
    {
        var layout = sequence.Layout;
        WriteCount(writer, sequence.Count);
        var hasZ = layout.HasZ();
        var hasM = layout.HasM();
        for (var i = 0; i < sequence.Count; i++)
        {
            var x = sequence.GetOrdinate(i, Ordinate.X);
            var y = sequence.GetOrdinate(i, Ordinate.Y);
            if (hasZ && hasM)
            {
                WriteOrdinates(writer, x, y, sequence.GetOrdinate(i, Ordinate.Z), sequence.GetOrdinate(i, Ordinate.M));
            }
            else if (hasZ)
            {
                WriteOrdinates(writer, x, y, sequence.GetOrdinate(i, Ordinate.Z));
            }
            else if (hasM)
            {
                WriteOrdinates(writer, x, y, sequence.GetOrdinate(i, Ordinate.M));
            }
            else
            {
                WriteOrdinates(writer, x, y);
            }
        }
    }

    private static void WriteOrdinates(Writer writer, params double[] ordinates)
    {
        foreach (var ordinate in ordinates)
        {
            writer.WriteDouble(ordinate);
        }
    }

    /// <summary>Writes a uint32 count (ring/point/part counts).</summary>
    private static void WriteCount(Writer writer, int count) => writer.WriteInt32(count);

    // ---- Shared exception + flag helpers ----

    private static PostgisEwkbFormatException Format(Reader cursor, string message) =>
        new($"{message} (at byte offset {cursor.Position})");

    private readonly record struct LayoutFlags(bool HasZ, bool HasM);

    private static LayoutFlags Of(uint type)
    {
        var hasZ = (type & ZFlag) != 0;
        var hasM = (type & MFlag) != 0;
        return new LayoutFlags(hasZ, hasM);
    }

    /// <summary>The coordinate layout the EWKB flags imply (never stored on the flags value itself so the
    /// flags type does not reference the core geometry model — the plugin's fan-in budget).</summary>
    private static CoordinateLayout LayoutOf(LayoutFlags flags) => LayoutOf(flags.HasZ, flags.HasM);

    private static CoordinateLayout LayoutOf(bool hasZ, bool hasM) => (hasZ, hasM) switch
    {
        (true, true) => CoordinateLayout.Xyzm,
        (true, false) => CoordinateLayout.Xyz,
        (false, true) => CoordinateLayout.Xym,
        (false, false) => CoordinateLayout.Xy,
    };

    /// <summary>A bounds-checked forward-only reader over the EWKB bytes.</summary>
    private sealed class Reader
    {
        private readonly byte[] _bytes;
        private int _position;

        public Reader(byte[] bytes)
        {
            _bytes = bytes;
        }

        public int Position => _position;

        public byte ReadByte()
        {
            Require(1);
            return _bytes[_position++];
        }

        public uint ReadUInt32(byte endian)
        {
            Require(4);
            var value = endian == Ndr
                ? BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(_position, 4))
                : BinaryPrimitives.ReadUInt32BigEndian(_bytes.AsSpan(_position, 4));
            _position += 4;
            return value;
        }

        public int ReadCount(byte endian, string what)
        {
            var value = ReadUInt32(endian);
            if (value > int.MaxValue)
            {
                throw Format(this, $"{what} {value} is too large");
            }

            return checked((int)value);
        }

        public double ReadDouble(byte endian)
        {
            Require(8);
            var value = endian == Ndr
                ? BinaryPrimitives.ReadDoubleLittleEndian(_bytes.AsSpan(_position, 8))
                : BinaryPrimitives.ReadDoubleBigEndian(_bytes.AsSpan(_position, 8));
            _position += 8;
            return value;
        }

        private void Require(int length)
        {
            if (_position + length > _bytes.Length)
            {
                throw new PostgisEwkbFormatException(
                    $"the EWKB payload ends early at byte offset {_position}: {length} more bytes needed but only {_bytes.Length - _position} remain");
            }
        }
    }

    /// <summary>A growable little-endian byte writer.</summary>
    private sealed class Writer
    {
        private readonly List<byte> _buffer = new();

        public void WriteByte(byte value) => _buffer.Add(value);

        public void WriteInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            _buffer.AddRange(bytes);
        }

        public void WriteUInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            _buffer.AddRange(bytes);
        }

        public void WriteDouble(double value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
            _buffer.AddRange(bytes);
        }

        public byte[] ToArray() => _buffer.ToArray();
    }
}

/// <summary>Stored geometry that cannot be decoded (malformed or unsupported EWKB).</summary>
internal sealed class PostgisEwkbFormatException(string message) : Exception(message)
{
}

/// <summary>A write whose geometry CRS conflicts with the dataset column's SRID.</summary>
internal sealed class PostgisCrsMismatchException(string message) : Exception(message)
{
}
