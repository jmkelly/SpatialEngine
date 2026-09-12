namespace RenderSpike;

using Spatial.Core.Geometry;

/// <summary>Deterministic sample geometry for the spike, in EPSG:4326 lon/lat.</summary>
public static class SpikeData
{
    public static IReadOnlyList<RenderLayer> Sample() =>
    [
        new("parks", Park()),
        new("roads", Roads()),
        new("cities", Cities()),
    ];

    public static IGeometry Park() => GeometryFactory.CreatePolygon(
    [
        new Coordinate(-0.185, 51.498),
        new Coordinate(-0.095, 51.498),
        new Coordinate(-0.088, 51.525),
        new Coordinate(-0.130, 51.540),
        new Coordinate(-0.190, 51.531),
        new Coordinate(-0.185, 51.498),
    ]);

    public static IGeometry Roads() => GeometryFactory.CreateMultiLineString(
    [
        GeometryFactory.CreateLineString(
        [
            new Coordinate(-0.235, 51.470),
            new Coordinate(-0.150, 51.500),
            new Coordinate(-0.060, 51.515),
            new Coordinate(0.020, 51.505),
        ]),
        GeometryFactory.CreateLineString(
        [
            new Coordinate(-0.190, 51.520),
            new Coordinate(-0.120, 51.480),
            new Coordinate(-0.030, 51.470),
            new Coordinate(0.030, 51.490),
        ]),
    ]);

    public static IGeometry Cities() => GeometryFactory.CreateMultiPoint(
    [
        GeometryFactory.CreatePoint(-0.128, 51.507),
        GeometryFactory.CreatePoint(-0.002, 51.505),
        GeometryFactory.CreatePoint(-0.220, 51.485),
        GeometryFactory.CreatePoint(-0.060, 51.520),
    ]);

    /// <summary>A dense, deterministic line network for throughput measurement.</summary>
    public static IReadOnlyList<RenderLayer> BenchmarkNetwork(int lines, int verticesPerLine)
    {
        var random = new Random(1234);
        var network = new LineString[lines];
        for (var line = 0; line < lines; line++)
        {
            var coordinates = new Coordinate[verticesPerLine];
            var lon = -0.24 + (random.NextDouble() * 0.36);
            var lat = 51.46 + (random.NextDouble() * 0.09);
            for (var vertex = 0; vertex < verticesPerLine; vertex++)
            {
                lon += (random.NextDouble() - 0.45) * 0.004;
                lat += (random.NextDouble() - 0.45) * 0.004;
                coordinates[vertex] = new Coordinate(lon, lat);
            }

            network[line] = GeometryFactory.CreateLineString(coordinates);
        }

        return [new RenderLayer("roads", GeometryFactory.CreateMultiLineString(network))];
    }
}
