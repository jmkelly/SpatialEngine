using System.Buffers.Binary;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// A test-side PostGIS EWKB byte builder (the plugin's writer is private; the
/// fixtures pin the reader and writer against independently built bytes).
/// Output is little-endian NDR by default like PostGIS; a big-endian point
/// fixture builds XDR explicitly. Known-good anchors come from PostGIS:
/// <c>SRID=4326;POINT(1 2)</c> is
/// <c>0101000020E6100000000000000000F03F0000000000000040</c>, and the empty
/// point is the NaN/NaN sentinel.
/// </summary>
internal static class EwkbFixture
{
    private const uint ZFlag = 0x80000000;
    private const uint MFlag = 0x40000000;
    private const uint SridFlag = 0x20000000;

    /// <summary>The known PostGIS bytes of SRID=4326;POINT(1 2).</summary>
    public static byte[] KnownPoint4326() => Convert.FromHexString("0101000020E6100000000000000000F03F0000000000000040");

    /// <summary>A POINT (1 2) with an optional srid.</summary>
    public static byte[] Point(int srid = 0) => Geometry(1u | (srid > 0 ? SridFlag : 0), srid, w => w.Double(1).Double(2));

    /// <summary>A POINT Z (1 2 z) without srid.</summary>
    public static byte[] PointZ(double z) => Geometry(1u | ZFlag, 0, w => w.Double(1).Double(2).Double(z));

    /// <summary>A POINT (x y z) with an optional srid.</summary>
    public static byte[] PointXyz(int srid, double x, double y, double z) =>
        Geometry(1u | ZFlag | (srid > 0 ? SridFlag : 0), srid, w => w.Double(x).Double(y).Double(z));

    /// <summary>A POINT (x y z m) with an optional srid.</summary>
    public static byte[] PointXyzm(int srid, double x, double y, double z, double m) =>
        Geometry(1u | ZFlag | MFlag | (srid > 0 ? SridFlag : 0), srid, w => w.Double(x).Double(y).Double(z).Double(m));

    /// <summary>A POINT M (x y m) without srid.</summary>
    public static byte[] PointM(double x, double y, double m) =>
        Geometry(1u | MFlag, 0, w => w.Double(x).Double(y).Double(m));

    /// <summary>The NaN/NaN empty-point sentinel.</summary>
    public static byte[] PointEmpty() => Geometry(1, 0, w => w.Double(double.NaN).Double(double.NaN));

    /// <summary>A LINESTRING from flat XY ordinates, with an optional srid.</summary>
    public static byte[] LineString(int srid = 0, params double[] flatCoordinates) =>
        Geometry(2u | (srid > 0 ? SridFlag : 0), srid, w =>
        {
            w.Int32(flatCoordinates.Length / 2);
            foreach (var value in flatCoordinates)
            {
                w.Double(value);
            }
        });

    /// <summary>An empty LINESTRING with an optional srid.</summary>
    public static byte[] LineStringEmpty(int srid = 0) =>
        Geometry(2u | (srid > 0 ? SridFlag : 0), srid, w => w.Int32(0));

    /// <summary>A POLYGON of flat XY rings, with an optional srid.</summary>
    public static byte[] Polygon(int srid = 0, params double[][] rings) =>
        Geometry(3u | (srid > 0 ? SridFlag : 0), srid, w =>
        {
            w.Int32(rings.Length);
            foreach (var ring in rings)
            {
                w.Int32(ring.Length / 2);
                foreach (var value in ring)
                {
                    w.Double(value);
                }
            }
        });

    /// <summary>An empty POLYGON with an optional srid.</summary>
    public static byte[] PolygonEmpty(int srid = 0) =>
        Geometry(3u | (srid > 0 ? SridFlag : 0), srid, w => w.Int32(0));

    /// <summary>A MULTI geometry of child byte arrays, with an optional srid.</summary>
    public static byte[] Multi(int type, int srid, params byte[][] children) =>
        Geometry((uint)type | (srid > 0 ? SridFlag : 0), srid, w =>
        {
            w.Int32(children.Length);
            foreach (var child in children)
            {
                w.AddRange(child);
            }
        });

    /// <summary>An empty MULTI geometry with an optional srid.</summary>
    public static byte[] MultiEmpty(int type, int srid) =>
        Geometry((uint)type | (srid > 0 ? SridFlag : 0), srid, w => w.Int32(0));

    /// <summary>Builds a big-endian (XDR) POINT without srid (endianness coverage).</summary>
    public static byte[] PointBigEndian(double x, double y)
    {
        var bytes = new byte[21];
        bytes[0] = 0; // XDR
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1, 4), 1);
        BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(5, 8), x);
        BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(13, 8), y);
        return bytes;
    }

    private static byte[] Geometry(uint typeWithFlags, int srid, Action<Writer> payload)
    {
        var writer = new Writer();
        writer.Byte(1);
        writer.UInt32(typeWithFlags);
        if ((typeWithFlags & SridFlag) != 0)
        {
            writer.Int32(srid);
        }

        payload(writer);
        return writer.ToArray();
    }

    internal sealed class Writer
    {
        private readonly List<byte> _bytes = new();

        public Writer Byte(byte value)
        {
            _bytes.Add(value);
            return this;
        }

        public Writer Double(double value)
        {
            Span<byte> span = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(span, value);
            _bytes.AddRange(span);
            return this;
        }

        public Writer Int32(int value)
        {
            Span<byte> span = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(span, value);
            _bytes.AddRange(span);
            return this;
        }

        public Writer UInt32(uint value)
        {
            Span<byte> span = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(span, value);
            _bytes.AddRange(span);
            return this;
        }

        public Writer AddRange(IEnumerable<byte> bytes)
        {
            _bytes.AddRange(bytes);
            return this;
        }

        public byte[] ToArray() => _bytes.ToArray();
    }
}
