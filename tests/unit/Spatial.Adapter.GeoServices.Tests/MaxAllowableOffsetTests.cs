using Microsoft.AspNetCore.Http;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;
using Spatial.Operations.NetTopologySuite;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// <c>maxAllowableOffset</c> and <c>quantizationParameters</c> on the query
/// response path (spec §9.1.4, SpatialEngine-u2x.3): both change the bytes a
/// client gets back, so both are pinned here at the projection seam — the
/// place the response geometry is shaped.
///
/// The fixture is a ~1 km-radius polygon sampled every 2 degrees, so a real
/// offset has real work to do: at an offset of 10 the boundary can be carried
/// by a handful of vertices, while at an offset of 0 no vertex may move.
/// </summary>
public sealed class MaxAllowableOffsetTests
{
    private static readonly ICoordinateTransforms NoTransforms = new NullTransforms();

    private static readonly IGeometryOperations Operations = new NtsGeometryOperations();

    private static readonly FeatureSchema Schema = new(
        [new FieldDefinition("shape", AttributeKind.Geometry, nullable: true)]);

    /// <summary>A closed 1000 m-radius ring: 180 vertices two degrees apart.</summary>
    private static Polygon Circle()
    {
        var ring = new Coordinate[181];
        for (var i = 0; i < ring.Length; i++)
        {
            var radians = 2 * Math.PI * i / 180;
            ring[i] = new Coordinate(1000 * Math.Cos(radians), 1000 * Math.Sin(radians));
        }

        return GeometryFactory.CreatePolygon(GeometryFactory.CreateLineString(ring));
    }

    private static MatchedFeature Feature(IGeometry geometry) =>
        new(1, new Feature(
            new FeatureId("1"),
            Schema,
            [AttributeValue.FromGeometry(geometry)]));

    private static async Task<EsriFeatureQuery> QueryAsync(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        return EsriFeatureQuery.Parse(await EsriRequestParameters.ReadAsync(context, CancellationToken.None), fallback: null);
    }

    private static IGeometry Project(IGeometry geometry, EsriFeatureQuery query) =>
        FeatureProjection.TransformFeature(Feature(geometry), query, null, NoTransforms, Operations, CancellationToken.None).Feature[0].GeometryValue!;

    [Fact]
    public async Task Max_allowable_offset_simplifies_the_response_geometry()
    {
        var circle = Circle();
        var query = await QueryAsync(("maxAllowableOffset", "10"));

        var projected = (Polygon)Project(circle, query);

        Assert.True(projected.ExteriorRing.Sequence.Count < circle.ExteriorRing.Sequence.Count);
        for (var i = 0; i < circle.ExteriorRing.Sequence.Count; i++)
        {
            var vertex = circle.ExteriorRing.Sequence.GetCoordinate(i);
            Assert.True(
                Distance(projected, vertex) <= 10,
                $"the input vertex ({vertex.X}, {vertex.Y}) is {Distance(projected, vertex)} from the response, over the 10 allowed.");
        }
    }

    [Fact]
    public async Task A_zero_offset_returns_the_full_precision_geometry()
    {
        var circle = Circle();
        var query = await QueryAsync(("maxAllowableOffset", "0"));

        var projected = Project(circle, query);

        Assert.Equal(circle.ExteriorRing.Sequence.Count, ((Polygon)projected).ExteriorRing.Sequence.Count);
    }

    [Fact]
    public async Task An_offset_wider_than_the_geometry_still_returns_the_geometry()
    {
        var query = await QueryAsync(("maxAllowableOffset", "100000"));

        var projected = Project(Circle(), query);

        Assert.IsType<Polygon>(projected);
        Assert.Equal(181, ((Polygon)projected).ExteriorRing.Sequence.Count);
    }

    [Fact]
    public async Task A_query_without_either_parameter_returns_the_stored_geometry()
    {
        var circle = Circle();

        var projected = Project(circle, await QueryAsync());

        Assert.Same(circle, projected);
    }

    [Fact]
    public async Task A_cancelled_query_propagates_cancellation()
    {
        var query = await QueryAsync(("maxAllowableOffset", "10"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Assert.Throws<OperationCanceledException>(() =>
            FeatureProjection.TransformFeature(Feature(Circle()), query, null, NoTransforms, Operations, cts.Token));
    }

    [Fact]
    public async Task Max_allowable_offset_is_range_checked()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => QueryAsync(("maxAllowableOffset", "-1")));
        await Assert.ThrowsAsync<EsriInteropException>(() => QueryAsync(("maxAllowableOffset", "nan")));
    }

    [Fact]
    public async Task Quantization_quantizes_every_ordinate_to_the_requested_grid()
    {
        var query = await QueryAsync((
            "quantizationParameters",
            """{"mode":"view","originPosition":"upperLeft","tolerance":100,"extent":{"xmin":0,"ymin":0,"xmax":2000,"ymax":2000}}"""));

        var projected = Project(Circle(), query);

        foreach (var vertex in Vertices(projected))
        {
            Assert.Equal(0, (int)Math.Round(vertex.X) % 100);
            Assert.Equal(0, (int)Math.Round(vertex.Y) % 100);
        }
    }

    [Fact]
    public async Task Quantization_anchors_the_grid_on_the_named_origin()
    {
        var upperLeft = await QueryAsync(("quantizationParameters", Quantum("upperLeft")));
        var lowerLeft = await QueryAsync(("quantizationParameters", Quantum("lowerLeft")));

        // The view's top edge is 2050 and its bottom is 0, so the two origins
        // are half a quantum apart and a point 40 above the axis snaps onto
        // the 50 row from the top and the 0 row from the bottom.
        Assert.Equal(50, QuantizedY(upperLeft));
        Assert.Equal(0, QuantizedY(lowerLeft));
    }

    private static string Quantum(string originPosition) =>
        "{\"mode\":\"view\",\"originPosition\":\"" + originPosition + "\",\"tolerance\":100,\"extent\":{\"xmin\":0,\"ymin\":0,\"xmax\":2000,\"ymax\":2050}}";

    private static double QuantizedY(EsriFeatureQuery query) =>
        Project(GeometryFactory.CreateLineString([new Coordinate(0, 40), new Coordinate(10, 60)]), query) is LineString line
            ? line.Sequence.GetCoordinate(0).Y
            : double.NaN;

    [Fact]
    public async Task A_broken_quantization_request_fails_with_invalid_arguments()
    {
        await Assert.ThrowsAsync<EsriInteropException>(() => QueryAsync(("quantizationParameters", "{")));
        await Assert.ThrowsAsync<EsriInteropException>(() => QueryAsync(("quantizationParameters", """{"mode":"view"}""")));
        await Assert.ThrowsAsync<EsriInteropException>(() => QueryAsync(("quantizationParameters", """{"mode":"polygon","tolerance":1,"extent":{"xmin":0,"ymin":0,"xmax":1,"ymax":1}}""")));
        await Assert.ThrowsAsync<EsriInteropException>(() => QueryAsync(("quantizationParameters", """{"mode":"view","tolerance":0,"extent":{"xmin":0,"ymin":0,"xmax":1,"ymax":1}}""")));
    }

    private static IEnumerable<Coordinate> Vertices(IGeometry geometry)
    {
        foreach (var part in geometry.DepthFirst())
        {
            switch (part)
            {
                case Point { Coordinate: { } point }:
                    yield return point;
                    break;
                case LineString line:
                    for (var i = 0; i < line.Sequence.Count; i++)
                    {
                        yield return line.Sequence.GetCoordinate(i);
                    }

                    break;
            }
        }
    }

    private static double Distance(IGeometry geometry, Coordinate vertex)
    {
        var closest = double.MaxValue;
        foreach (var part in geometry.DepthFirst())
        {
            if (part is not LineString line || line.Sequence.Count < 2)
            {
                continue;
            }

            for (var i = 0; i + 1 < line.Sequence.Count; i++)
            {
                closest = Math.Min(closest, SegmentDistance(vertex, line.Sequence.GetCoordinate(i), line.Sequence.GetCoordinate(i + 1)));
            }
        }

        return closest;
    }

    /// <summary>The planar distance from a point to a segment.</summary>
    private static double SegmentDistance(Coordinate point, Coordinate start, Coordinate end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = (dx * dx) + (dy * dy);
        if (length <= 0)
        {
            return Math.Sqrt(((point.X - start.X) * (point.X - start.X)) + ((point.Y - start.Y) * (point.Y - start.Y)));
        }

        var t = Math.Clamp((((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / length, 0, 1);
        var dxp = point.X - (start.X + (t * dx));
        var dyp = point.Y - (start.Y + (t * dy));
        return Math.Sqrt((dxp * dxp) + (dyp * dyp));
    }

    private sealed class NullTransforms : ICoordinateTransforms
    {
        public IGeometry Transform(IGeometry geometry, string? source, string target, CancellationToken cancellationToken = default) => geometry;
    }
}
