using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// WFS 2.0.0 read-path parity (T-046): startIndex paging, srsName
/// reprojection, GML honesty, sortBy ordering, filter honesty,
/// numberMatched/numberReturned/next envelope and stored-query/value
/// rejects-by-name. Red first: every test fails on the pre-T-046 service.
/// </summary>
public sealed class WfsServiceTests
{
    private static Map WfsMap() => OgcFixtures.Map(MapService.Wfs);

    private static void Seed(OgcFixtures.FakeStore store)
    {
        store.Seed(
            OgcFixtures.City("amsterdam", 900_000, 4.9, 52.3),
            OgcFixtures.City("berlin", 3_600_000, 13.4, 52.5),
            OgcFixtures.City("cairo", 9_500_000, 31.2, 30.0),
            OgcFixtures.City("dublin", 550_000, -6.2, 53.3),
            OgcFixtures.City("edinburgh", 500_000, -3.1, 55.9));
    }

    private static async Task<(string? ContentType, string Body)> GetFeatureAsync(
        Map map,
        OgcRequestServices services,
        string query,
        ICoordinateTransforms? transforms = null,
        CancellationToken cancellationToken = default)
    {
        var request = new DefaultHttpContext();
        request.Request.Method = "GET";
        request.Request.Scheme = "http";
        request.Request.Host = new HostString("localhost");
        request.Request.Path = new PathString($"/ogc/{map.Name}/wfs");
        request.Request.QueryString = new QueryString(query);
        var parameters = await OgcParameters.ReadAsync(request, cancellationToken);
        var effective = transforms is null
            ? services
            : new OgcRequestServices(services.Stores, services.Registry, services.Renderer, transforms);
        var response = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        response.Response.Body = new MemoryStream();
        var result = await WfsService.HandleAsync(map.Name, parameters, effective, new OgcOptions(), request, cancellationToken);
        await result.ExecuteAsync(response);
        response.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(response.Response.Body).ReadToEndAsync(CancellationToken.None);
        return (response.Response.ContentType?.Split(';')[0].Trim(), body);
    }

    private static async Task<OgcServiceException> GetFeatureFailureAsync(Map map, OgcRequestServices services, string query)
    {
        var request = new DefaultHttpContext();
        request.Request.QueryString = new QueryString(query);
        var parameters = await OgcParameters.ReadAsync(request, CancellationToken.None);
        var context = new DefaultHttpContext();
        return await Assert.ThrowsAsync<OgcServiceException>(() =>
            WfsService.HandleAsync(map.Name, parameters, services, new OgcOptions(), context, CancellationToken.None));
    }

    [Fact]
    public async Task GetFeature_pages_with_start_index_in_stable_order()
    {
        var map = WfsMap();
        var (services, store) = OgcFixtures.Build(map);
        Seed(store);

        var (_, body) = await GetFeatureAsync(
            map, services, "?service=WFS&request=GetFeature&typeNames=Cities&count=2&startIndex=2");
        var root = JsonDocument.Parse(body).RootElement;
        var ids = root.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("id").GetString()).ToArray();

        Assert.True(ids.SequenceEqual(["Cities.cairo", "Cities.dublin"]), $"Unexpected page order: {string.Join(',', ids)}");
        Assert.Equal(5, root.GetProperty("numberMatched").GetInt32());
        Assert.Equal(2, root.GetProperty("numberReturned").GetInt32());
        Assert.True(root.TryGetProperty("next", out var next) && next.GetString()!.Contains("startIndex=4"));
    }

    [Fact]
    public async Task GetFeature_last_page_has_no_next()
    {
        var map = WfsMap();
        var (services, store) = OgcFixtures.Build(map);
        Seed(store);

        var (_, body) = await GetFeatureAsync(
            map, services, "?service=WFS&request=GetFeature&typeNames=Cities&count=2&startIndex=4");
        var root = JsonDocument.Parse(body).RootElement;

        Assert.Equal(5, root.GetProperty("numberMatched").GetInt32());
        Assert.Equal(1, root.GetProperty("numberReturned").GetInt32());
        Assert.False(root.TryGetProperty("next", out _));
    }

    [Fact]
    public async Task GetFeature_rejects_a_negative_start_index()
    {
        var map = WfsMap();
        var (services, store) = OgcFixtures.Build(map);
        Seed(store);

        var failure = await GetFeatureFailureAsync(
            map, services, "?service=WFS&request=GetFeature&typeNames=Cities&startIndex=-1");

        Assert.Equal("InvalidParameterValue", failure.Code);
        Assert.Contains("startIndex", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetFeature_reprojects_geometries_to_srs_name()
    {
        var map = WfsMap();
        var (services, store) = OgcFixtures.Build(map);
        store.Seed(OgcFixtures.City("amsterdam", 900_000, 4.0, 52.0));

        var (_, body) = await GetFeatureAsync(
            map, services, "?service=WFS&request=GetFeature&typeNames=Cities&srsName=EPSG:3857",
            transforms: new DoublingTransforms());
        var coordinates = JsonDocument.Parse(body).RootElement
            .GetProperty("features")[0].GetProperty("geometry").GetProperty("coordinates");

        Assert.Equal(8.0, coordinates[0].GetDouble());
        Assert.Equal(104.0, coordinates[1].GetDouble());
    }

    [Fact]
    public async Task GetFeature_rejects_an_unknown_srs_name()
    {
        var map = WfsMap();
        var (services, store) = OgcFixtures.Build(map);
        Seed(store);

        var failure = await GetFeatureFailureAsync(
            map, services, "?service=WFS&request=GetFeature&typeNames=Cities&srsName=EPSG:9999");

        Assert.Equal("InvalidParameterValue", failure.Code);
    }

    [Fact]
    public async Task GetFeature_keeps_the_honest_gml_reject()
    {
        var map = WfsMap();
        var (services, store) = OgcFixtures.Build(map);
        Seed(store);

        var failure = await GetFeatureFailureAsync(
            map, services, "?service=WFS&request=GetFeature&typeNames=Cities&outputFormat=application/gml+xml");

        Assert.Equal("InvalidParameterValue", failure.Code);
        Assert.Contains("geo+json", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Capabilities_advertise_only_the_served_output_formats()
    {
        var map = WfsMap();
        var (services, store) = OgcFixtures.Build(map);
        store.Seed(OgcFixtures.City("amsterdam", 900_000, 4.9, 52.3));
        var layer = await services.LoadAsync(map, map.Layers[0], CancellationToken.None);

        var xml = await WfsCapabilities.BuildAsync(
            map, [layer], "http://localhost/ogc/world/wfs", services, new OgcOptions(), CancellationToken.None);

        Assert.Contains("application/geo+json", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gml", xml, StringComparison.OrdinalIgnoreCase);
        var document = System.Xml.Linq.XDocument.Parse(xml);
        var ows = System.Xml.Linq.XNamespace.Get("http://www.opengis.net/ows/1.1");
        var outputFormat = document.Descendants(ows + "Parameter")
            .FirstOrDefault(element => string.Equals(element.Attribute("name")?.Value, "outputFormat", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(outputFormat);
        var allowed = outputFormat.Descendants(ows + "Value").Select(element => element.Value).ToArray();
        Assert.Contains("application/geo+json", allowed);
        Assert.DoesNotContain(allowed, value => value.Contains("gml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetFeature_sorts_by_an_attribute_descending()
    {
        var map = WfsMap();
        var (services, store) = OgcFixtures.Build(map);
        Seed(store);

        var (_, body) = await GetFeatureAsync(
            map, services, "?service=WFS&request=GetFeature&typeNames=Cities&sortBy=population%20D&count=2");
        var names = JsonDocument.Parse(body).RootElement
            .GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("properties").GetProperty("name").GetString()).ToArray();

        Assert.True(names.SequenceEqual(["cairo", "berlin"]), $"Unexpected sort order: {string.Join(',', names)}");
    }

    [Fact]
    public async Task GetFeature_rejects_sort_by_an_unknown_property()
    {
        var map = WfsMap();
        var (services, store) = OgcFixtures.Build(map);
        Seed(store);

        var failure = await GetFeatureFailureAsync(
            map, services, "?service=WFS&request=GetFeature&typeNames=Cities&sortBy=nope");

        Assert.Equal("InvalidParameterValue", failure.Code);
        Assert.Contains("sortBy", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("propertyName")]
    [InlineData("aliases")]
    [InlineData("resolve")]
    [InlineData("resolveDepth")]
    [InlineData("resolveTimeout")]
    public async Task GetFeature_honestly_rejects_unimplemented_projection_and_resolve_by_name(string parameter)
    {
        var map = WfsMap();
        var (services, store) = OgcFixtures.Build(map);
        Seed(store);

        var failure = await GetFeatureFailureAsync(
            map, services, $"?service=WFS&request=GetFeature&typeNames=Cities&{parameter}=name");

        Assert.Equal("InvalidParameterValue", failure.Code);
        Assert.Contains(parameter, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("filter")]
    [InlineData("cql_filter")]
    [InlineData("resourceid")]
    public async Task GetFeature_honestly_rejects_unimplemented_filters_by_name(string parameter)
    {
        var map = WfsMap();
        var (services, store) = OgcFixtures.Build(map);
        Seed(store);

        var failure = await GetFeatureFailureAsync(
            map, services, $"?service=WFS&request=GetFeature&typeNames=Cities&{parameter}=name%3D%27x%27");

        Assert.Equal("InvalidParameterValue", failure.Code);
        Assert.Contains(parameter, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetFeature_reports_number_matched_and_returned()
    {
        var map = WfsMap();
        var (services, store) = OgcFixtures.Build(map);
        Seed(store);

        var (_, body) = await GetFeatureAsync(
            map, services, "?service=WFS&request=GetFeature&typeNames=Cities&count=2");
        var root = JsonDocument.Parse(body).RootElement;

        Assert.Equal(5, root.GetProperty("numberMatched").GetInt32());
        Assert.Equal(2, root.GetProperty("numberReturned").GetInt32());
    }

    [Theory]
    [InlineData("GetPropertyValue")]
    [InlineData("ListStoredQueries")]
    [InlineData("DescribeStoredQueries")]
    [InlineData("CreateStoredQuery")]
    [InlineData("DropStoredQuery")]
    [InlineData("Transaction")]
    [InlineData("LockFeature")]
    public async Task Unsupported_operations_reject_by_name(string operation)
    {
        var map = WfsMap();
        var (services, _) = OgcFixtures.Build(map);
        var request = new DefaultHttpContext();
        request.Request.QueryString = new QueryString($"?service=WFS&request={operation}");
        var parameters = await OgcParameters.ReadAsync(request, CancellationToken.None);
        var context = new DefaultHttpContext();

        var failure = await Assert.ThrowsAsync<OgcServiceException>(() =>
            WfsService.HandleAsync(map.Name, parameters, services, new OgcOptions(), context, CancellationToken.None));

        Assert.Equal("OperationNotSupported", failure.Code);
        Assert.Contains(operation, failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetFeature_honours_cancellation()
    {
        var map = WfsMap();
        var (services, store) = OgcFixtures.Build(map);
        Seed(store);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var request = new DefaultHttpContext();
        request.Request.QueryString = new QueryString("?service=WFS&request=GetFeature&typeNames=Cities");
        var parameters = await OgcParameters.ReadAsync(request, CancellationToken.None);
        var context = new DefaultHttpContext();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WfsService.HandleAsync(map.Name, parameters, services, new OgcOptions(), context, cancelled.Token));
    }

    private sealed class DoublingTransforms : ICoordinateTransforms
    {
        public IGeometry Transform(IGeometry geometry, string? source, string target, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return geometry switch
            {
                Point point when point.Coordinate is { } coordinate => GeometryFactory.CreatePoint(
                    coordinate.X * 2, coordinate.Y * 2, CoordinateReference.Epsg(3857)),
                _ => geometry,
            };
        }
    }
}
