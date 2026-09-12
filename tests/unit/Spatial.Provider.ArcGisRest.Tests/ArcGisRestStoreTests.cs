using System.Net;
using System.Text;
using Spatial.Core.Features;
using Spatial.PluginSdk;

namespace Spatial.Provider.ArcGisRest.Tests;

public sealed class ArcGisRestStoreTests
{
    private const string BaseUrl = "https://example.com/arcgis/rest/services/demo/FeatureServer";

    private const string LayerMetadata = """
        {
          "id": 0,
          "name": "Cities",
          "type": "Feature Layer",
          "geometryType": "esriGeometryPoint",
          "objectIdField": "OBJECTID",
          "spatialReference": { "wkid": 4326 },
          "maxRecordCount": 2,
          "fields": [
            { "name": "OBJECTID", "type": "esriFieldTypeOID", "nullable": false },
            { "name": "name", "type": "esriFieldTypeString", "nullable": true },
            { "name": "population", "type": "esriFieldTypeInteger", "nullable": true }
          ]
        }
        """;

    private const string ServiceRoot = """
        { "layers": [ { "id": 0, "name": "Cities" } ], "tables": [] }
        """;

    [Fact]
    public async Task List_returns_the_remote_layers()
    {
        var handler = Handler(Route(ServiceRoot, LayerMetadata));
        var store = Store(handler);

        var datasets = await store.ListAsync();

        var dataset = Assert.Single(datasets);
        Assert.Equal("arcgis.l0", dataset.Id);
        Assert.Equal("l0", dataset.Table);
        Assert.Equal("geometry", dataset.GeometryColumn);
        Assert.Equal(4326, dataset.Srid);
    }

    [Fact]
    public async Task Describe_maps_the_schema()
    {
        var handler = Handler(Route(ServiceRoot, LayerMetadata));
        var store = Store(handler);

        var description = await store.DescribeAsync("arcgis.l0");

        Assert.Equal("Point", description.GeometryType);
        Assert.Equal(4326, description.Srid);
        Assert.Equal("OBJECTID", Assert.Single(description.IdColumns));
        Assert.Contains(description.Schema.Fields, field => field is { Name: "name", Kind: AttributeKind.String });
        Assert.Contains(description.Schema.Fields, field => field.Name == "geometry" && field.Kind == AttributeKind.Geometry);
    }

    [Fact]
    public async Task Describe_canonicalises_a_named_esri_geometry_field()
    {
        // Real layers declare geometry as Shape/SHAPE; the engine's canonical
        // column is "geometry", so the Esri-named field must not leak through.
        const string metadata = """
            {
              "id": 0, "name": "Cities", "geometryType": "esriGeometryPoint", "objectIdField": "OBJECTID",
              "spatialReference": { "wkid": 4326 },
              "fields": [
                { "name": "OBJECTID", "type": "esriFieldTypeOID" },
                { "name": "SHAPE", "type": "esriFieldTypeGeometry" },
                { "name": "name", "type": "esriFieldTypeString" }
              ]
            }
            """;
        var store = Store(Handler(Route(ServiceRoot, metadata)));

        var description = await store.DescribeAsync("arcgis.l0");

        Assert.Equal("geometry", Assert.Single(description.Schema.Fields, field => field.Kind == AttributeKind.Geometry).Name);
        Assert.DoesNotContain(description.Schema.Fields, field => field.Name == "SHAPE");
    }

    [Fact]
    public async Task Describe_derives_the_object_id_from_the_oid_field()
    {
        // Older/MapServer layers omit objectIdField; the esriFieldTypeOID
        // field is the identity.
        const string metadata = """
            {
              "id": 0, "name": "Stations", "geometryType": "esriGeometryPoint",
              "spatialReference": { "wkid": 4326 },
              "fields": [
                { "name": "objectid", "type": "esriFieldTypeOID" },
                { "name": "SHAPE", "type": "esriFieldTypeGeometry" },
                { "name": "name", "type": "esriFieldTypeString" }
              ]
            }
            """;
        var store = Store(Handler(Route(ServiceRoot, metadata)));

        var description = await store.DescribeAsync("arcgis.l0");

        Assert.Equal("objectid", Assert.Single(description.IdColumns));
    }

    [Fact]
    public async Task Scan_decodes_geometry_from_a_named_esri_geometry_field()
    {
        const string metadata = """
            {
              "id": 0, "name": "Cities", "geometryType": "esriGeometryPoint", "objectIdField": "OBJECTID",
              "spatialReference": { "wkid": 4326 },
              "fields": [
                { "name": "OBJECTID", "type": "esriFieldTypeOID" },
                { "name": "SHAPE", "type": "esriFieldTypeGeometry" },
                { "name": "name", "type": "esriFieldTypeString" }
              ]
            }
            """;
        var handler = Handler(Route(ServiceRoot, metadata, QueryPage(
            """{"attributes":{"OBJECTID":7,"name":"Berlin"},"geometry":{"x":13.405,"y":52.52}}""")));
        var store = Store(handler);

        var batches = await store.ScanAsync("arcgis.l0");

        var feature = Assert.Single(Assert.Single(batches).Features);
        Assert.NotNull(feature["geometry"].GeometryValue);
    }

    [Fact]
    public async Task Scan_decodes_features_into_a_canonical_batch()
    {
        var handler = Handler(Route(ServiceRoot, LayerMetadata, QueryPage(
            """{"attributes":{"OBJECTID":7,"name":"Berlin","population":3664000},"geometry":{"x":13.405,"y":52.52}}""")));
        var store = Store(handler);

        var batches = await store.ScanAsync("arcgis.l0");

        var batch = Assert.Single(batches);
        var feature = Assert.Single(batch.Features);
        Assert.Equal("7", feature.Id.Value);
        Assert.Equal("Berlin", feature["name"].StringValue);
        Assert.Equal(13.405, feature["geometry"].GeometryValue.Envelope!.Value.MinX, 4);
    }

    [Fact]
    public async Task Scan_follows_pagination()
    {
        var handler = Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/query", StringComparison.Ordinal))
            {
                return request.RequestUri.Query.Contains("resultOffset=1", StringComparison.Ordinal)
                    ? Json(QueryPage("""{"attributes":{"OBJECTID":2,"name":"B","population":20},"geometry":{"x":2,"y":2}}""", exceeded: false))
                    : Json(QueryPage("""{"attributes":{"OBJECTID":1,"name":"A","population":10},"geometry":{"x":1,"y":1}}""", exceeded: true));
            }

            return path.EndsWith("/0", StringComparison.Ordinal) ? Json(LayerMetadata) : Json(ServiceRoot);
        });
        var store = Store(handler);

        var batches = await store.ScanAsync("arcgis.l0");

        Assert.Equal(2, batches[0].Count);
    }

    [Fact]
    public async Task Query_renders_bbox_and_translated_where()
    {
        var handler = Handler(Route(ServiceRoot, LayerMetadata, QueryPage()));
        var store = Store(handler);

        await store.QueryAsync(
            "arcgis.l0",
            new BoundingBox(1, 2, 3, 4),
            "name = 'Berlin' AND population > 1000");

        var query = Assert.Single(handler.Requests, request => request.Contains("/query", StringComparison.Ordinal));
        Assert.Contains("geometry=", query, StringComparison.Ordinal);
        Assert.Contains("spatialRel=esriSpatialRelEnvelopeIntersects", query, StringComparison.Ordinal);
        Assert.Contains("where=%28name%20%3D%20%27Berlin%27%20AND%20population%20%3E%201000%29", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_rejects_an_untranslatable_filter()
    {
        var handler = Handler(Route(ServiceRoot, LayerMetadata, QueryPage()));
        var store = Store(handler);

        var exception = await Assert.ThrowsAsync<SpatialException>(() => store.QueryAsync("arcgis.l0", filter: "DROP TABLE"));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public async Task An_unsupported_field_type_is_rejected()
    {
        const string metadata = """
            {
              "id": 0, "name": "Rasters", "geometryType": "esriGeometryPoint", "objectIdField": "OBJECTID",
              "spatialReference": { "wkid": 4326 },
              "fields": [
                { "name": "OBJECTID", "type": "esriFieldTypeOID" },
                { "name": "raster", "type": "esriFieldTypeRaster" }
              ]
            }
            """;
        var handler = Handler(Route(ServiceRoot, metadata));
        var store = Store(handler);

        var exception = await Assert.ThrowsAsync<SpatialException>(() => store.DescribeAsync("arcgis.l0"));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public async Task A_remote_error_is_mapped()
    {
        var handler = Handler(_ => Json("""{"error":{"code":400,"message":"Invalid query"}}""", HttpStatusCode.BadRequest));
        var store = Store(handler);

        var exception = await Assert.ThrowsAsync<SpatialException>(() => store.ScanAsync("arcgis.l0"));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public async Task An_unreachable_service_is_store_unavailable()
    {
        var handler = Handler(_ => throw new HttpRequestException("connection refused"));
        var store = Store(handler);

        var exception = await Assert.ThrowsAsync<SpatialException>(() => store.ScanAsync("arcgis.l0"));

        Assert.Equal(SpatialException.StoreUnavailable, exception.Code);
    }

    [Fact]
    public async Task Writes_and_creation_are_rejected()
    {
        var store = Store(Handler(Route(ServiceRoot, LayerMetadata)));

        await Assert.ThrowsAsync<SpatialException>(() => store.WriteAsync("arcgis.l0", new FeatureBatch(new FeatureSchema([]), [])));
        await Assert.ThrowsAsync<SpatialException>(() => store.CreateAsync("arcgis.l0", new FeatureBatch(new FeatureSchema([]), []), 4326));
    }

    [Fact]
    public async Task A_malformed_dataset_id_is_rejected()
    {
        var store = Store(Handler(Route(ServiceRoot, LayerMetadata)));

        var exception = await Assert.ThrowsAsync<SpatialException>(() => store.DescribeAsync("demo.cities"));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public async Task The_token_is_appended_to_requests()
    {
        var handler = Handler(Route(ServiceRoot, LayerMetadata));
        var options = new ArcGisRestServiceOptions { Name = "remote", Url = BaseUrl };
        var store = new ArcGisRestStore(new HttpClient(handler), options, "secret-token");

        await store.ListAsync();

        Assert.All(handler.Requests, request => Assert.Contains("token=secret-token", request, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"id":0,"name":"Cities","geometryType":"esriGeometryPoint","objectIdField":"FID","spatialReference":{"wkid":4326},"fields":[{"name":"FID","type":"esriFieldTypeOID"}]}""", "FID")]
    [InlineData("""{"id":0,"name":"Cities","geometryType":"esriGeometryPoint","spatialReference":{"wkid":4326},"fields":[{"name":"OID","type":"esriFieldTypeOID"}]}""", "OID")]
    [InlineData("""{"id":0,"name":"Cities","geometryType":"esriGeometryPoint","spatialReference":{"wkid":4326},"fields":[]}""", "OBJECTID")]
    [InlineData("""{"id":0,"name":"Cities","geometryType":"esriGeometryPoint","spatialReference":{"wkid":4326}}""", "OBJECTID")]
    public async Task Object_id_field_resolves_from_metadata(string metadata, string expectedIdField)
    {
        var handler = Handler(Route(ServiceRoot, metadata));
        var store = Store(handler);

        var description = await store.DescribeAsync("arcgis.l0");

        Assert.Equal(expectedIdField, Assert.Single(description.IdColumns));
    }

    private static ArcGisRestStore Store(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new ArcGisRestServiceOptions { Name = "remote", Url = BaseUrl });

    private static StubHandler Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) => new(responder);

    private static Func<HttpRequestMessage, HttpResponseMessage> Route(string root, string metadata, string? query = null) => request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/query", StringComparison.Ordinal) && query is not null)
        {
            return Json(query);
        }

        return path.EndsWith("/0", StringComparison.Ordinal) ? Json(metadata) : Json(root);
    };

    private static string QueryPage(string? feature = null, bool exceeded = false) =>
        feature is null
            ? $$"""{"features":[],"exceededTransferLimit":{{exceeded.ToString().ToLowerInvariant()}}}"""
            : $$"""{"features":[{{feature}}],"exceededTransferLimit":{{exceeded.ToString().ToLowerInvariant()}}}""";

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(responder(request));
        }
    }
}
