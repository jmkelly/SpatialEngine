using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;

namespace Spatial.Performance;

/// <summary>
/// Esri feature codec micros (T-076): encode and decode for a point and a
/// 128-vertex polygon feature. Encode allocates its writer per call (that
/// allocation is part of the measured path); decode works on a document
/// parsed once in setup so the bench measures the codec, not
/// <c>System.Text.Json</c> parsing.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class EsriCodecBenchmarks
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("population", AttributeKind.Int64),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private Feature _point = null!;
    private Feature _polygon = null!;
    private JsonDocument _pointDocument = null!;
    private JsonDocument _polygonDocument = null!;

    [GlobalSetup]
    public void Setup()
    {
        _point = new Feature(
            new FeatureId("amsterdam"),
            Schema,
            [
                AttributeValue.FromString("Amsterdam"),
                AttributeValue.FromInt64(900_000),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(4.9041, 52.3676, CoordinateReference.Epsg(4326))),
            ]);
        _polygon = new Feature(
            new FeatureId("district"),
            Schema,
            [
                AttributeValue.FromString("District"),
                AttributeValue.FromInt64(12_000),
                AttributeValue.FromGeometry(Circle(4.9, 52.37, 0.05, 128)),
            ]);

        _pointDocument = JsonDocument.Parse(Encode(_point));
        _polygonDocument = JsonDocument.Parse(Encode(_polygon));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _pointDocument.Dispose();
        _polygonDocument.Dispose();
    }

    [Benchmark(Description = "EsriFeatureCodec encode (point)")]
    public long Encode_Point() => Encode(_point).Length;

    [Benchmark(Description = "EsriFeatureCodec encode (128-gon)")]
    public long Encode_Polygon() => Encode(_polygon).Length;

    [Benchmark(Description = "EsriFeatureCodec decode (point)")]
    public object Decode_Point() =>
        EsriFeatureCodec.Decode(_pointDocument.RootElement, Schema, "OBJECTID", "geometry", CoordinateReference.Epsg(4326));

    [Benchmark(Description = "EsriFeatureCodec decode (128-gon)")]
    public object Decode_Polygon() =>
        EsriFeatureCodec.Decode(_polygonDocument.RootElement, Schema, "OBJECTID", "geometry", CoordinateReference.Epsg(4326));

    private static byte[] Encode(Feature feature)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        EsriFeatureCodec.Write(writer, feature, new EsriFeatureWriteOptions("OBJECTID", 7));
        writer.Flush();
        return stream.ToArray();
    }

    private static Polygon Circle(double cx, double cy, double radius, int vertices)
    {
        var ring = new Coordinate[vertices + 1];
        for (var i = 0; i <= vertices; i++)
        {
            var angle = 2 * Math.PI * i / vertices;
            ring[i] = new Coordinate(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
        }

        return GeometryFactory.CreatePolygon(ring, CoordinateReference.Epsg(4326));
    }
}
