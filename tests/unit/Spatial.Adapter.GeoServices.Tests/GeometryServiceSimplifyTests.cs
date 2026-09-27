using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;
using Spatial.Operations.NetTopologySuite;
using Spatial.Transformations.ProjNet;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The geometry service's <c>simplify</c> operation (spec §7.0.5) is
/// generalization, not topological repair: the tolerance arrives as
/// <c>deviation</c> or, mutually exclusively, <c>value</c>, and the engine's
/// Douglas-Peucker verb consumes it. The reproduction here is a
/// thousand-vertex polygon, which the pre-fix wiring returned whole because
/// the engine was asked to repair an already-valid shape.
/// </summary>
public sealed class GeometryServiceSimplifyTests
{
    private static readonly NtsGeometryOperations Operations = new();

    private static readonly NtsGeometryMeasures Measures = new();

    private static readonly GeometryServiceCapabilities Capabilities = new(
        Operations,
        Measures,
        new NtsGeometryProcessing(),
        new NtsGeometryRelations(),
        new ProjNetTransforms(),
        new ProjNetTransforms(),
        new ProjNetGeodesicBuffering(Operations, new NtsGeometryProcessing()));

    /// <summary>The reproduction: a thousand-vertex ring generalized by five units.</summary>
    private const int VertexCount = 1000;

    private const double Deviation = 5;

    /// <summary>Fixed-point ordinates: the Esri codec reads numbers from their text and rejects exponent notation.</summary>
    private const string Ordinate = "0.###############";

    [Fact]
    public async Task Simplify_generalizes_a_thousand_vertex_polygon_within_the_deviation()
    {
        var ring = WobblyRing(VertexCount);
        var result = await DispatchAsync(("geometries", Ring(ring)), ("deviation", Deviation.ToString(CultureInfo.InvariantCulture)));

        var simplified = EsriValueParser.ParseGeometries(result.ToString(), fallback: null)[0];
        Assert.True(
            RingVertexCount(simplified) < VertexCount,
            $"simplify kept all {VertexCount} vertices, so the tolerance was ignored.");
        Assert.InRange(DeviationOf(ring, simplified), 0, Deviation);
    }

    [Fact]
    public async Task Simplify_honours_the_value_tolerance()
    {
        // Spec §7.0.5.1: `value` is the alternative to `deviation`, in the
        // units of the spatial reference, and the two are mutually exclusive.
        var ring = WobblyRing(VertexCount);
        var result = await DispatchAsync(("geometries", Ring(ring)), ("value", "0.5"));

        var simplified = EsriValueParser.ParseGeometries(result.ToString(), fallback: null)[0];
        Assert.True(RingVertexCount(simplified) < VertexCount);
        Assert.True(DeviationOf(ring, simplified) <= 0.5);
    }

    [Fact]
    public async Task Simplify_requires_a_tolerance()
    {
        var error = await Assert.ThrowsAsync<EsriInteropException>(
            () => DispatchAsync(("geometries", Ring(WobblyRing(8)))));

        Assert.Equal(EsriErrorCodes.InvalidParameters, error.Code);
        Assert.Contains("deviation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Simplify_rejects_deviation_and_value_together()
    {
        var error = await Assert.ThrowsAsync<EsriInteropException>(
            () => DispatchAsync(
                ("geometries", Ring(WobblyRing(8))),
                ("deviation", "5"),
                ("value", "5")));

        Assert.Equal(EsriErrorCodes.InvalidParameters, error.Code);
        Assert.Contains("mutually exclusive", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("deviation", "-1")]
    [InlineData("value", "-1")]
    [InlineData("deviation", "NaN")]
    public async Task Simplify_rejects_an_unusable_tolerance(string parameter, string value)
    {
        var error = await Assert.ThrowsAsync<EsriInteropException>(
            () => DispatchAsync(("geometries", Ring(WobblyRing(8))), (parameter, value)));

        Assert.Equal(EsriErrorCodes.InvalidParameters, error.Code);
        Assert.Contains(parameter, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The deliberate change away from MakeValid: a self-intersecting ring is
    /// generalized like any other, so the result keeps one ring instead of
    /// being split into the two the repair verb would produce. The engine
    /// still owns <c>IGeometryProcessing.Repair</c>; no Esri operation names
    /// it, so it must not be aliased to <c>simplify</c>.
    /// </summary>
    [Fact]
    public async Task Simplify_generalizes_a_self_intersecting_ring_instead_of_repairing_it()
    {
        const string bowtie = """[{"rings":[[[0,0],[2,2],[2,0],[0,2],[0,0]]]}]""";

        var result = await DispatchAsync(("geometries", bowtie), ("deviation", "0.5"));

        var rings = result.GetProperty("geometries")[0].GetProperty("rings");
        Assert.Equal(1, rings.GetArrayLength());
        Assert.Equal(4, rings[0].GetArrayLength());
    }

    [Fact]
    public async Task Info_advertises_simplify_alongside_generalize()
    {
        // Both names are served and both are generalization: `generalize`
        // takes `maxDeviation`, `simplify` takes `deviation`/`value`.
        var capabilities = (await ExecuteAsync(GeometryService.Info())).GetProperty("capabilities").GetString();

        Assert.NotNull(capabilities);
        Assert.Contains("Generalize", capabilities, StringComparison.Ordinal);
        Assert.Contains("Simplify", capabilities, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Simplify_honours_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var parameters = await ParamsAsync(("geometries", Ring(WobblyRing(8))), ("deviation", "5"));

        Assert.Throws<OperationCanceledException>(
            () => GeometryService.Dispatch("simplify", parameters, Capabilities, cancelled.Token));
    }

    /// <summary>
    /// The worst displacement the generalized result makes of any input
    /// vertex, measured with the engine's own distance verb. The tolerance is
    /// honoured when this stays within it.
    /// </summary>
    private static double DeviationOf(IReadOnlyList<Coordinate> ring, IGeometry simplified) =>
        ring.Max(coordinate => Measures.Distance(
            GeometryFactory.CreatePoint(coordinate.X, coordinate.Y, null), simplified, CancellationToken.None));

    private static int RingVertexCount(IGeometry geometry) =>
        geometry is IPolygon polygon ? polygon.ExteriorRing.CoordinateCount : -1;

    /// <summary>A closed ring of <paramref name="count"/> vertices on a circle, every other one pushed out.</summary>
    private static IReadOnlyList<Coordinate> WobblyRing(int count) =>
        [.. Enumerable.Range(0, count)
            .Select(index =>
            {
                var angle = (2 * Math.PI * index) / count;
                var radius = 10 + (index % 2 == 0 ? 0.2 : 0);
                return new Coordinate(radius * Math.Cos(angle), radius * Math.Sin(angle));
            })];

    /// <summary>The Esri JSON polygon whose exterior ring is the given (closed) ring.</summary>
    private static string Ring(IReadOnlyList<Coordinate> ring)
    {
        var closed = new List<Coordinate>(ring) { ring[0] };
        var coordinates = string.Join(
            ",",
            closed.Select(coordinate => string.Create(CultureInfo.InvariantCulture, $"[{coordinate.X.ToString(Ordinate, CultureInfo.InvariantCulture)},{coordinate.Y.ToString(Ordinate, CultureInfo.InvariantCulture)}]")));
        return $"[{{\"rings\":[[{coordinates}]]}}]";
    }

    private static async Task<JsonElement> DispatchAsync(params (string Key, string Value)[] values) =>
        await ExecuteAsync(GeometryService.Dispatch("simplify", await ParamsAsync(values), Capabilities, CancellationToken.None));

    private static async Task<JsonElement> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    private static async Task<EsriRequestParameters> ParamsAsync(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        return await EsriRequestParameters.ReadAsync(context, CancellationToken.None);
    }
}
