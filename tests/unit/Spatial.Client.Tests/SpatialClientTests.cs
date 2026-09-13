using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.Core.Geometry;
using Spatial.Core.Geometry.Codec;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Http;

namespace Spatial.Client.Tests;

/// <summary>
/// The client SDK's contract handling: URL building, the shared camelCase
/// JSON wire, canonical geometry/batch Base64 round trips and structured
/// error mapping — all against a scriptable stub host.
/// </summary>
public sealed class SpatialClientTests
{
    [Fact]
    public async Task Buffer_posts_sgeom_and_decodes_the_result()
    {
        var buffered = GeometryFactory.CreatePolygon(
            [new Coordinate(-1, -1), new Coordinate(1, -1), new Coordinate(1, 1), new Coordinate(-1, 1), new Coordinate(-1, -1)]);
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json(
            $$$"""{"geometry":"{{{Convert.ToBase64String(GeometryCodec.Encode(buffered))}}}"}""")));

        var result = await stub.Client.BufferAsync(GeometryFactory.CreatePoint(0, 0), 1.0);

        Assert.Equal(buffered, result);
        var exchange = Assert.Single(stub.Handler.Exchanges);
        Assert.Equal(HttpMethod.Post, exchange.Request.Method);
        Assert.Equal("/api/geometry/buffer", exchange.Request.RequestUri?.AbsolutePath);
        var body = JsonDocument.Parse(await exchange.Request.Content!.ReadAsStringAsync());
        Assert.Equal(1.0, body.RootElement.GetProperty("distance").GetDouble());
        Assert.Equal(8, body.RootElement.GetProperty("quadrantSegments").GetInt32());
    }

    [Fact]
    public async Task Validate_decodes_the_flag()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json("""{"valid":true}""")));

        Assert.True(await stub.Client.ValidateAsync(GeometryFactory.CreatePoint(0, 0)));
    }

    [Fact]
    public async Task Describe_decodes_the_crs_description()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json(
            """{"authority":"EPSG","code":"4326","name":"WGS 84","kind":"geographic","dimension":2,"axes":[],"datum":null,"ellipsoid":null}""")));

        var description = await stub.Client.DescribeAsync("EPSG:4326");

        Assert.Equal("4326", description.Code);
        var exchange = Assert.Single(stub.Handler.Exchanges);
        Assert.Equal("/api/crs/describe", exchange.Request.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task Scan_decodes_canonical_batches()
    {
        var batch = new FeatureBatch(
            new FeatureSchema([new FieldDefinition("name", AttributeKind.String)]),
            [new Feature(new FeatureId("a"), new FeatureSchema([new FieldDefinition("name", AttributeKind.String)]), [AttributeValue.FromString("a")])]);
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json(
            $$$"""{"batches":["{{{Convert.ToBase64String(FeatureBatchCodec.Encode(batch))}}}"]}""")));

        var batches = await stub.Client.ScanAsync("demo.points");

        var features = Assert.Single(batches).Features;
        Assert.Equal("a", Assert.Single(features).Id.Value);
    }

    [Fact]
    public async Task Catalogue_lists_datasets()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json(
            """{"datasets":[{"id":"demo.points","schema":"demo","table":"points","geometryColumn":"geometry","srid":4326,"estimatedRowCount":110}]}""")));

        var datasets = await stub.Client.ListCatalogueAsync();

        var summary = Assert.Single(datasets);
        Assert.Equal("demo.points", summary.Id);
        var exchange = Assert.Single(stub.Handler.Exchanges);
        Assert.StartsWith("/api/catalogue", exchange.Request.RequestUri?.AbsolutePath);
        Assert.Contains("store=demo", exchange.Request.RequestUri?.Query);
    }

    [Fact]
    public async Task Write_returns_the_appended_count()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json("""{"appended":3}""")));

        var batch = new FeatureBatch(
            new FeatureSchema([new FieldDefinition("name", AttributeKind.String)]), []);
        Assert.Equal(3, await stub.Client.WriteAsync("public.t", batch, store: "postgis"));
    }

    [Fact]
    public async Task Transactions_round_trip_handles()
    {
        using var stub = new StubClient(new StubHttpHandler(request => StubHttpHandler.Json(
            request.RequestUri?.AbsolutePath switch
            {
                "/api/transactions/begin" => """{"transaction":"abc"}""",
                "/api/transactions/commit" => """{"ok":true}""",
                _ => """{"ok":true}""",
            })));

        var handle = await stub.Client.BeginTransactionAsync();
        Assert.Equal("abc", handle);
        Assert.True(await stub.Client.CommitTransactionAsync(handle));
    }

    [Fact]
    public async Task Host_errors_throw_structured_exceptions()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json(
            """{"code":"invalid.arguments","message":"bad distance"}""", HttpStatusCode.BadRequest)));

        var exception = await Assert.ThrowsAsync<SpatialClientException>(() =>
            stub.Client.BufferAsync(GeometryFactory.CreatePoint(0, 0), double.NaN));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("invalid.arguments", exception.Code);
    }

    [Fact]
    public async Task Non_json_errors_throw_status_only_exceptions()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("not json"),
        }));

        var exception = await Assert.ThrowsAsync<SpatialClientException>(() =>
            stub.Client.BufferAsync(GeometryFactory.CreatePoint(0, 0), 1.0));

        Assert.Equal(500, exception.StatusCode);
        Assert.Equal("http.error", exception.Code);
    }

    [Fact]
    public void The_base_address_overload_constructs_without_a_host()
    {
        var client = new SpatialClient("http://localhost:9/");

        Assert.NotNull(client);
    }

    [Fact]
    public async Task Sleep_returns_the_duration()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json("""{"slept":50}""")));

        Assert.Equal(50, await stub.Client.SleepAsync(50));
    }

    [Fact]
    public async Task Render_reads_image_bytes_and_metadata_headers()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => Image([1, 2, 3], "image/png", 400, 250)));
        using var style = JsonDocument.Parse("""{"version":8,"layers":[]}""");
        var request = new RenderRequest(
            new ViewportDto(-10, 35, 30, 60, 400, 250, "EPSG:4326"),
            style.RootElement.Clone(),
            [new RenderLayerDto("demo.cities", "demo")]);

        var image = await stub.Client.RenderAsync(request);

        Assert.Equal([1, 2, 3], image.Content);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(400, image.Width);
        Assert.Equal(250, image.Height);
        Assert.Equal(RasterFormat.Png, image.Format);
        Assert.Equal("/api/render", Assert.Single(stub.Handler.Exchanges).Request.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task Render_maps_a_host_failure_to_a_client_exception()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json(
            """{"code":"invalid.arguments","message":"bad"}""", HttpStatusCode.BadRequest)));
        using var style = JsonDocument.Parse("""{"version":8,"layers":[]}""");
        var request = new RenderRequest(
            new ViewportDto(0, 0, 1, 1, 10, 10, "EPSG:4326"),
            style.RootElement.Clone(),
            []);

        var exception = await Assert.ThrowsAsync<SpatialClientException>(() => stub.Client.RenderAsync(request));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("invalid.arguments", exception.Code);
    }

    [Fact]
    public async Task Render_capabilities_are_read_from_the_host()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json(
            """{"formats":["png"],"pixelFormats":["rgba8888"],"blendModes":["over"],"maxPixels":100,"imagerySources":[{"name":"basemap"}]}""")));

        var capabilities = await stub.Client.RenderCapabilitiesAsync();

        Assert.Equal("png", Assert.Single(capabilities.Formats));
        Assert.Equal("basemap", Assert.Single(capabilities.ImagerySources).Name);
    }

    [Fact]
    public async Task RenderMap_posts_to_the_map_route_and_reads_the_image()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => Image([7, 7], "image/png", 400, 250)));
        var request = new MapRenderRequestDto(new ViewportDto(-10, 35, 30, 60, 400, 250, "EPSG:4326"));

        var image = await stub.Client.RenderMapAsync("cities", request);

        Assert.Equal([7, 7], image.Content);
        Assert.Equal(400, image.Width);
        Assert.Equal("/api/maps/cities/render", Assert.Single(stub.Handler.Exchanges).Request.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task RenderTile_posts_to_the_tile_route_and_reads_the_image()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => Image([9, 9], "image/png", 256, 256)));
        using var style = JsonDocument.Parse("""{"version":8,"layers":[]}""");
        var request = new TileRenderRequest(style.RootElement.Clone(), [new RenderLayerDto("demo.cities", "demo")]);

        var image = await stub.Client.Tiles.RenderAsync(3, 1, 2, request);

        Assert.Equal(256, image.Width);
        Assert.Equal("/api/render/tiles/3/1/2.png", Assert.Single(stub.Handler.Exchanges).Request.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task RenderTiles_reads_the_ordered_batch()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json(
            """{"tiles":[{"z":0,"x":0,"y":0,"cached":false,"contentType":"image/png","width":256,"height":256,"content":"AQID"}]}""")));
        using var style = JsonDocument.Parse("""{"version":8,"layers":[]}""");
        var request = new TileBatchRequest(new TileRenderRequest(style.RootElement.Clone(), []), [new TileDto(0, 0, 0)]);

        var response = await stub.Client.Tiles.RenderAsync(request);

        var tile = Assert.Single(response.Tiles);
        Assert.False(tile.Cached);
        Assert.Equal([1, 2, 3], Convert.FromBase64String(tile.Content));
        Assert.Equal("/api/render/tiles/batch", Assert.Single(stub.Handler.Exchanges).Request.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task Tile_capabilities_are_read_from_the_host()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json(
            """{"defaultScheme":"webmercator","maxTilesPerBatch":64,"schemes":[{"id":"webmercator","crs":"EPSG:3857","tileSize":256,"minZoom":0,"maxZoom":23,"levels":[{"zoom":0,"resolution":1,"scaleDenominator":2}]}]}""")));

        var capabilities = await stub.Client.Tiles.CapabilitiesAsync();

        Assert.Equal("webmercator", capabilities.DefaultScheme);
        Assert.Equal(64, capabilities.MaxTilesPerBatch);
        Assert.Equal("EPSG:3857", Assert.Single(capabilities.Schemes).Crs);
    }

    private static HttpResponseMessage Image(byte[] bytes, string mediaType, int width, int height)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        response.Headers.Add("X-Raster-Width", width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.Add("X-Raster-Height", height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return response;
    }
}
