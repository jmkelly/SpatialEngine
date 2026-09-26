using System.Buffers.Binary;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Stores.SqlServer.Geometry;

/// <summary>
/// The plugin's **single** <c>Spatial.Core.Geometry</c> surface (ADR-0028
/// fan-in budget, mirrored for the SQL Server provider): the WKB ↔
/// core-geometry interchange. Everything else in the store passes geometry
/// around as <see cref="AttributeValue"/> (a WKB payload on read, an
/// attribute on write) and never names a core geometry type.
/// <para>
/// The payload is OGC well-known binary as SQL Server's
/// <c>geometry::STGeomFromWKB</c> reads and <c>STAsBinary</c> writes: a
/// byte-order marker, a 32-bit type and little-endian ordinates. The
/// interchange is byte-deterministic (always little-endian NDR), handles
/// per-child byte order and the NaN-sentinel empty point, and stamps the
/// CRS identity (<c>EPSG:&lt;srid&gt;</c>) it is told to use on the
/// top-level geometry — the SRID itself is carried by the column and the
/// provider metadata, not by the payload.
/// </para>
/// <para>
/// SQL Server's <c>geometry</c>/<c>geography</c> columns are planar
/// two-dimensional, so only the XY layout round-trips: a write whose
/// geometry carries Z or M fails with
/// <see cref="SqlServerLayoutNotSupportedException"/> naming the layout,
/// rather than silently dropping the ordinate. Malformed stored bytes fail
/// with <see cref="SqlServerWkbFormatException"/>; both are mapped by the
/// runners to structured contract errors.
/// </para>
/// </summary>
internal static class SqlServerWkb
{
    private const byte Ndr = 1;

    /// <summary>
    /// Decodes a stored geometry into an attribute value (WKB → core geometry
    /// → attribute), stamped with the dataset's CRS identity. SRID 0 is SQL
    /// Server's planar marker and stays an unknown (null) CRS, as in the
    /// PostGIS provider.
    /// </summary>
    public static AttributeValue ReadGeometry(byte[] wkb, int srid) =>
        AttributeValue.FromGeometry(Decode(wkb, srid > 0 ? CoordinateReference.Epsg(srid) : null));

    /// <summary>
    /// Encodes a geometry attribute for storage. When the geometry carries a
    /// CRS it must agree with the dataset's SRID (a conflict is
    /// <see cref="SqlServerCrsMismatchException"/>); a CRS-less geometry is
    /// stored at <paramref name="datasetSrid"/>.
    /// </summary>
    public static byte[] WriteGeometry(AttributeValue value, int datasetSrid)
    {
        var geometry = value.GeometryValue;
        if (geometry.Layout != CoordinateLayout.Xy)
        {
            throw new SqlServerLayoutNotSupportedException(
                $"SQL Server spatial columns store XY coordinates only, but this geometry is {LayoutName(geometry.Layout)}; " +
                "drop the Z and M ordinates before writing it.");
        }

        if (geometry.CoordinateReference is { } stamped && !Matches(stamped, datasetSrid))
        {
            throw new SqlServerCrsMismatchException(
                $"the geometry carries {stamped} but the dataset column stores SRID {datasetSrid}; transform it first or write to a matching column.");
        }

        return Encode(geometry);
    }

    /// <summary>The bounding envelope of a query as a WKB polygon (or line/point when the box is flat).</summary>
    public static byte[] Envelope(double minx, double miny, double maxx, double maxy)
    {
        var writer = new Writer();
        if (minx == maxx && miny == maxy)
        {
            WriteType(writer, GeometryType.Point);
            writer.WriteDouble(minx);
            writer.WriteDouble(miny);
            return writer.ToArray();
        }

        if (minx == maxx || miny == maxy)
        {
            WriteType(writer, GeometryType.LineString);
            writer.WriteCount(2);
            WriteOrdinates(writer, minx, miny);
            WriteOrdinates(writer, maxx, maxy);
            return writer.ToArray();
        }

        // A SQL Server polygon ring must be closed (first point repeated), or
        // STGeomFromWKB refuses the whole envelope.
        WriteType(writer, GeometryType.Polygon);
        writer.WriteCount(1);
        writer.WriteCount(5);
        WriteOrdinates(writer, minx, miny);
        WriteOrdinates(writer, maxx, miny);
        WriteOrdinates(writer, maxx, maxy);
        WriteOrdinates(writer, minx, maxy);
        WriteOrdinates(writer, minx, miny);
        return writer.ToArray();
    }

    /// <summary>The display name of a value's coordinate layout, for diagnostics.</summary>
    public static string LayoutName(AttributeValue value) => value.Kind == AttributeKind.Geometry
        ? LayoutName(value.GeometryValue.Layout)
        : "not geometry";

    /// <summary>Whether SQL Server can store this value's coordinate layout (XY only).</summary>
    public static bool IsStorable(AttributeValue value) =>
        value.IsNull || value.Kind != AttributeKind.Geometry || value.GeometryValue.Layout == CoordinateLayout.Xy;

    /// <summary>The display name of a coordinate layout, for diagnostics.</summary>
    public static string LayoutName(CoordinateLayout layout) => layout switch
    {
        CoordinateLayout.Xy => "XY",
        CoordinateLayout.Xyz => "XYZ",
        CoordinateLayout.Xym => "XYM",
        CoordinateLayout.Xyzm => "XYZM",
        _ => layout.ToString(),
    };

    private static bool Matches(CoordinateReference reference, int srid) =>
        reference.Authority.Equals("EPSG", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(reference.Code, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var code)
        && code == srid;

    // ---- Reading (WKB bytes → IGeometry) ----

    private static IGeometry Decode(byte[] bytes, CoordinateReference? reference)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var cursor = new Reader(bytes);
        return ReadGeometryValue(cursor, reference);
    }

    private static IGeometry ReadGeometryValue(Reader cursor, CoordinateReference? reference)
    {
        var endian = cursor.ReadByte();
        var type = cursor.ReadUInt32(endian);
        if (type > (uint)GeometryType.GeometryCollection)
        {
            throw Format(cursor, $"WKB geometry type {type} is not supported; SQL Server stores the XY layout only");
        }

        if (!GeometryReaders.TryGetValue((GeometryType)type, out var read))
        {
            throw Format(cursor, $"WKB geometry type {type} is not supported");
        }

        return read(cursor, endian, reference);
    }

    /// <summary>
    /// Table-driven body dispatch: the concrete geometry type selects the
    /// small typed reader, so the dispatcher carries no per-case branch and
    /// a new core geometry type needs exactly one entry here.
    /// </summary>
    private static readonly Dictionary<GeometryType, Func<Reader, byte, CoordinateReference?, IGeometry>> GeometryReaders = new()
    {
        [GeometryType.Point] = (cursor, endian, reference) => ReadPoint(cursor, endian, reference),
        [GeometryType.LineString] = (cursor, endian, reference) => ReadLineString(cursor, endian, reference),
        [GeometryType.Polygon] = (cursor, endian, reference) => ReadPolygon(cursor, endian, reference),
        [GeometryType.MultiPoint] = (cursor, endian, reference) => ReadMultiPoint(cursor, endian, reference),
        [GeometryType.MultiLineString] = (cursor, endian, reference) => ReadMultiLineString(cursor, endian, reference),
        [GeometryType.MultiPolygon] = (cursor, endian, reference) => ReadMultiPolygon(cursor, endian, reference),
        [GeometryType.GeometryCollection] = (cursor, endian, reference) => ReadCollection(cursor, endian, reference),
    };

    private static Point ReadPoint(Reader cursor, byte endian, CoordinateReference? reference)
    {
        var x = cursor.ReadDouble(endian);
        var y = cursor.ReadDouble(endian);
        return double.IsNaN(x) && double.IsNaN(y)
            ? GeometryFactory.CreateEmptyPoint(reference)
            : GeometryFactory.CreatePoint(new Coordinate(x, y, null, null), reference);
    }

    private static LineString ReadLineString(Reader cursor, byte endian, CoordinateReference? reference)
    {
        var count = cursor.ReadCount(endian, "line string coordinate count");
        return count == 0
            ? GeometryFactory.CreateEmptyLineString(CoordinateLayout.Xy, reference)
            : GeometryFactory.CreateLineString(ReadCoordinates(cursor, endian, count), CoordinateLayout.Xy, reference);
    }

    private static Polygon ReadPolygon(Reader cursor, byte endian, CoordinateReference? reference)
    {
        var ringCount = cursor.ReadCount(endian, "polygon ring count");
        if (ringCount == 0)
        {
            return new Polygon(GeometryFactory.CreateEmptyLineString(CoordinateLayout.Xy), null, reference);
        }

        var rings = new LineString[ringCount];
        for (var i = 0; i < ringCount; i++)
        {
            var pointCount = cursor.ReadCount(endian, "polygon ring point count");
            rings[i] = new LineString(PackedCoordinateSequence.FromCoordinates(ReadCoordinates(cursor, endian, pointCount), CoordinateLayout.Xy));
        }

        return GeometryFactory.CreatePolygon(rings[0], rings.Skip(1), reference);
    }

    private static MultiPoint ReadMultiPoint(Reader cursor, byte endian, CoordinateReference? reference)
    {
        var points = new Point[cursor.ReadCount(endian, "multipoint count")];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = ReadAsType<Point>(cursor, GeometryType.Point, reference);
        }

        return GeometryFactory.CreateMultiPoint(points, reference);
    }

    private static MultiLineString ReadMultiLineString(Reader cursor, byte endian, CoordinateReference? reference)
    {
        var lines = new LineString[cursor.ReadCount(endian, "multilinestring count")];
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = ReadAsType<LineString>(cursor, GeometryType.LineString, reference);
        }

        return GeometryFactory.CreateMultiLineString(lines, reference);
    }

    private static MultiPolygon ReadMultiPolygon(Reader cursor, byte endian, CoordinateReference? reference)
    {
        var polygons = new Polygon[cursor.ReadCount(endian, "multipolygon count")];
        for (var i = 0; i < polygons.Length; i++)
        {
            polygons[i] = ReadAsType<Polygon>(cursor, GeometryType.Polygon, reference);
        }

        return GeometryFactory.CreateMultiPolygon(polygons, reference);
    }

    private static GeometryCollection ReadCollection(Reader cursor, byte endian, CoordinateReference? reference)
    {
        var parts = new IGeometry[cursor.ReadCount(endian, "collection count")];
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = ReadGeometryValue(cursor, reference);
        }

        return GeometryFactory.CreateGeometryCollection(parts, reference);
    }

    /// <summary>Reads a child geometry and requires it to be exactly the expected type.</summary>
    private static T ReadAsType<T>(Reader cursor, GeometryType expected, CoordinateReference? reference)
        where T : IGeometry
    {
        var part = ReadGeometryValue(cursor, reference);
        if (part.Type != expected)
        {
            throw Format(cursor, $"expected a {expected} child, found {part.Type}");
        }

        return (T)part;
    }

    private static Coordinate[] ReadCoordinates(Reader cursor, byte endian, int count)
    {
        var coordinates = new Coordinate[count];
        for (var i = 0; i < count; i++)
        {
            coordinates[i] = new Coordinate(cursor.ReadDouble(endian), cursor.ReadDouble(endian), null, null);
        }

        return coordinates;
    }

    // ---- Writing (IGeometry → WKB bytes) ----

    private static byte[] Encode(IGeometry geometry)
    {
        var writer = new Writer();
        WriteGeometry(writer, geometry);
        return writer.ToArray();
    }

    private static void WriteGeometry(Writer writer, IGeometry geometry)
    {
        if (!GeometryWriters.TryGetValue(geometry.Type, out var write))
        {
            throw new NotSupportedException($"cannot write WKB for geometry type {geometry.Type}");
        }

        WriteType(writer, geometry.Type);
        write(writer, geometry);
    }

    /// <summary>
    /// Table-driven body dispatch: the concrete geometry type selects the
    /// small typed writer, so the dispatcher carries no per-case branch.
    /// </summary>
    private static readonly Dictionary<GeometryType, Action<Writer, IGeometry>> GeometryWriters = new()
    {
        [GeometryType.Point] = (writer, geometry) => WritePoint(writer, (IPoint)geometry),
        [GeometryType.LineString] = (writer, geometry) => WriteSequence(writer, ((ILineString)geometry).Sequence),
        [GeometryType.Polygon] = (writer, geometry) => WritePolygon(writer, (IPolygon)geometry),
        [GeometryType.MultiPoint] = (writer, geometry) => WriteMulti(writer, ((IMultiPoint)geometry).Points),
        [GeometryType.MultiLineString] = (writer, geometry) => WriteMulti(writer, ((IMultiLineString)geometry).LineStrings),
        [GeometryType.MultiPolygon] = (writer, geometry) => WriteMulti(writer, ((IMultiPolygon)geometry).Polygons),
        [GeometryType.GeometryCollection] = (writer, geometry) => WriteMulti(writer, ((IGeometryParts)geometry).Geometries),
    };

    private static void WritePoint(Writer writer, IPoint point)
    {
        if (point.Coordinate is not { } coordinate)
        {
            writer.WriteDouble(double.NaN);
            writer.WriteDouble(double.NaN);
            return;
        }

        writer.WriteDouble(coordinate.X);
        writer.WriteDouble(coordinate.Y);
    }

    private static void WritePolygon(Writer writer, IPolygon polygon)
    {
        if (polygon.IsEmpty)
        {
            writer.WriteCount(0);
            return;
        }

        writer.WriteCount(polygon.InteriorRings.Count + 1);
        WriteSequence(writer, polygon.ExteriorRing.Sequence);
        foreach (var ring in polygon.InteriorRings)
        {
            WriteSequence(writer, ring.Sequence);
        }
    }

    private static void WriteMulti<T>(Writer writer, IReadOnlyList<T> parts)
        where T : IGeometry
    {
        writer.WriteCount(parts.Count);
        foreach (var part in parts)
        {
            WriteGeometry(writer, part);
        }
    }

    private static void WriteSequence(Writer writer, ICoordinateSequence sequence)
    {
        writer.WriteCount(sequence.Count);
        for (var i = 0; i < sequence.Count; i++)
        {
            WriteOrdinates(writer, sequence.GetOrdinate(i, Ordinate.X), sequence.GetOrdinate(i, Ordinate.Y));
        }
    }

    private static void WriteOrdinates(Writer writer, double x, double y)
    {
        writer.WriteDouble(x);
        writer.WriteDouble(y);
    }

    private static void WriteType(Writer writer, GeometryType type)
    {
        writer.WriteByte(Ndr);
        writer.WriteUInt32((uint)type);
    }

    private static SqlServerWkbFormatException Format(Reader cursor, string message) =>
        new($"{message} (at byte offset {cursor.Position})");

    /// <summary>A bounds-checked forward-only reader over the WKB bytes.</summary>
    private sealed class Reader(byte[] bytes)
    {
        private int _position;

        public int Position => _position;

        public byte ReadByte()
        {
            Require(1);
            return bytes[_position++];
        }

        public uint ReadUInt32(byte endian)
        {
            Require(4);
            var value = endian == Ndr
                ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(_position, 4))
                : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(_position, 4));
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

            return (int)value;
        }

        public double ReadDouble(byte endian)
        {
            Require(8);
            var value = endian == Ndr
                ? BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(_position, 8))
                : BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan(_position, 8));
            _position += 8;
            return value;
        }

        private void Require(int length)
        {
            if (_position + length > bytes.Length)
            {
                throw new SqlServerWkbFormatException(
                    $"the WKB payload ends early at byte offset {_position}: {length} more bytes needed but only {bytes.Length - _position} remain");
            }
        }
    }

    /// <summary>A growable little-endian byte writer.</summary>
    private sealed class Writer
    {
        private readonly List<byte> _buffer = [];

        public void WriteByte(byte value) => _buffer.Add(value);

        public void WriteCount(int value) => WriteInt32(value);

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

        public byte[] ToArray() => [.. _buffer];
    }
}

/// <summary>Stored geometry that cannot be decoded (malformed or unsupported WKB).</summary>
internal sealed class SqlServerWkbFormatException(string message) : Exception(message)
{
}

/// <summary>A write whose geometry CRS conflicts with the dataset column's SRID.</summary>
internal sealed class SqlServerCrsMismatchException(string message) : Exception(message)
{
}

/// <summary>A write whose geometry carries Z or M ordinates SQL Server cannot store.</summary>
internal sealed class SqlServerLayoutNotSupportedException(string message) : Exception(message)
{
}
