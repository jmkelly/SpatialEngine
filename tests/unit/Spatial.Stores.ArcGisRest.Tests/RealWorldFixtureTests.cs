using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Spatial.Core.Features;
using Spatial.PluginSdk;

namespace Spatial.Stores.ArcGisRest.Tests;

/// <summary>
/// Compliance tests reverse-engineered from responses recorded from real,
/// public ArcGIS REST services (the corpus and its discovery loop live under
/// <c>research/arcgis</c>; the responses under
/// <c>tests/fixtures/arcgis/captured</c>).
///
/// Each recorded layer is replayed through <see cref="ArcGisRestStore"/> and
/// the decoded result is checked against the Esri JSON the server actually
/// returned, so the suite pins the provider to the dialect real servers speak
/// rather than to hand-written samples. The targeted facts at the bottom
/// capture the specific gaps the corpus surfaced.
/// </summary>
public sealed class RealWorldFixtureTests
{
    private static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "arcgis-fixtures");

    private static readonly string[] QueryableGeometryTypes =
    [
        "esriGeometryPoint", "esriGeometryMultipoint", "esriGeometryPolyline", "esriGeometryPolygon",
    ];

    private static readonly string[] ActivityDatasets = ["arcgis.l0", "arcgis.l1"];

    public static TheoryData<string, string, int> QueryableLayers()
    {
        var data = new TheoryData<string, string, int>();
        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureRoot, "index.json")));
        foreach (var endpoint in index.RootElement.GetProperty("endpoints").EnumerateArray())
        {
            var url = endpoint.GetProperty("url").GetString()!;
            var directory = endpoint.GetProperty("directory").GetString()!;
            foreach (var layer in endpoint.GetProperty("layers").EnumerateArray())
            {
                var geometryType = layer.TryGetProperty("geometryType", out var element) ? element.GetString() : null;
                if (geometryType is not null && QueryableGeometryTypes.Contains(geometryType, StringComparer.Ordinal))
                {
                    data.Add(url, directory, layer.GetProperty("id").GetInt32());
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(QueryableLayers))]
    public async Task Recorded_layer_decodes_exactly_as_the_service_returned_it(string url, string directory, int layerId)
    {
        var folder = Path.Combine(FixtureRoot, directory);
        var metadata = ReadEnvelope(Path.Combine(folder, $"layer-{layerId}.json"));
        Assert.NotNull(metadata);
        using var metadataDocument = JsonDocument.Parse(metadata.Body);
        var layer = metadataDocument.RootElement;

        var handler = new FixtureHandler(url, folder);
        var store = new ArcGisRestStore(new HttpClient(handler), new ArcGisRestServiceOptions { Name = "remote", Url = url });

        var description = await store.DescribeAsync($"arcgis.l{layerId.ToString(CultureInfo.InvariantCulture)}");

        var objectIdField = ExpectedObjectIdField(layer);
        Assert.Equal("geometry", description.GeometryColumn);
        Assert.Equal(objectIdField, Assert.Single(description.IdColumns));
        Assert.Equal(ExpectedGeometryType(layer), description.GeometryType);

        var geometryField = Assert.Single(description.Schema.Fields, field => field.Kind == AttributeKind.Geometry);
        Assert.Equal("geometry", geometryField.Name);
        Assert.Equal(ExpectedSrid(layer), description.Srid);
        AssertFieldMapping(layer, description.Schema);

        var query = ReadEnvelope(Path.Combine(folder, $"query-{layerId}.json"));
        if (query is null)
        {
            return;
        }

        using var queryDocument = JsonDocument.Parse(query.Body);
        if (queryDocument.RootElement.TryGetProperty("error", out _))
        {
            var failure = await Assert.ThrowsAsync<SpatialException>(
                () => store.ScanAsync($"arcgis.l{layerId.ToString(CultureInfo.InvariantCulture)}"));
            Assert.Equal(SpatialException.InvalidArguments, failure.Code);
            return;
        }

        var batches = await store.ScanAsync($"arcgis.l{layerId.ToString(CultureInfo.InvariantCulture)}");
        var decoded = batches.SelectMany(batch => batch.Features).ToArray();
        var rawFeatures = queryDocument.RootElement.GetProperty("features").EnumerateArray().ToArray();
        Assert.Equal(rawFeatures.Length, decoded.Length);

        for (var i = 0; i < rawFeatures.Length; i++)
        {
            var attributes = rawFeatures[i].GetProperty("attributes");
            var rawId = Attribute(attributes, objectIdField).GetInt64();
            Assert.Equal(rawId.ToString(CultureInfo.InvariantCulture), decoded[i].Id.Value);

            var rawGeometry = rawFeatures[i].TryGetProperty("geometry", out var geometry)
                && geometry.ValueKind != JsonValueKind.Null;
            var decodedGeometry = decoded[i]["geometry"];
            if (rawGeometry)
            {
                Assert.NotEqual(AttributeKind.Null, decodedGeometry.Kind);
                Assert.NotNull(decodedGeometry.GeometryValue);
            }
            else
            {
                Assert.Equal(AttributeKind.Null, decodedGeometry.Kind);
            }
        }
    }

    private static JsonElement Attribute(JsonElement attributes, string name)
    {
        if (attributes.TryGetProperty(name, out var exact))
        {
            return exact;
        }

        foreach (var property in attributes.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new KeyNotFoundException($"No attribute '{name}' in the recorded feature.");
    }

    [Fact]
    public async Task List_returns_every_layer_of_a_real_service()
    {
        const string directory = "arcx-rest-services-edw-edw-activityfactscommonattributes-01-mapserver";
        const string url = "https://apps.fs.usda.gov/arcx/rest/services/EDW/EDW_ActivityFactsCommonAttributes_01/MapServer";
        var store = Store(url, directory);

        var datasets = await store.ListAsync();

        Assert.Equal(ActivityDatasets, datasets.Select(dataset => dataset.Id));
        Assert.All(datasets, dataset => Assert.Equal("geometry", dataset.GeometryColumn));
    }

    [Fact]
    public async Task A_table_layer_is_listed_but_has_no_geometry()
    {
        // ADR-0035 gap: the engine dataset contract is spatial, but a
        // FeatureServer table has no geometry. The provider still exposes it
        // with the canonical geometry column, whose features decode as null.
        // This characterises the current behaviour; see research/arcgis/README.md.
        const string directory = "0mseuqkaxrlepj5g-cases-time-v3-featureserver";
        const string url = "https://services1.arcgis.com/0MSEUqKaxRlEPj5g/arcgis/rest/services/cases_time_v3/FeatureServer";
        var store = Store(url, directory);

        var description = await store.DescribeAsync("arcgis.l0");
        var batches = await store.ScanAsync("arcgis.l0");

        Assert.Equal("Unknown", description.GeometryType);
        var feature = batches.SelectMany(batch => batch.Features).First();
        Assert.Equal(AttributeKind.Null, feature["geometry"].Kind);
    }

    [Fact]
    public async Task A_group_layer_is_not_listed_as_a_dataset()
    {
        // Group layers are containers, not queryable data (real MapServers
        // such as geonames/govunits are mostly group layers). Replaying a real
        // service root where layer 0 is a group over layer 1's captured
        // metadata must list only the feature layer.
        const string directory = "arcx-rest-services-edw-edw-activityfactscommonattributes-01-mapserver";
        const string url = "https://apps.fs.usda.gov/arcx/rest/services/EDW/EDW_ActivityFactsCommonAttributes_01/MapServer";
        const string serviceRoot = """
            {"layers":[
              {"id":0,"name":"Labels","type":"Group Layer","subLayerIds":[1]},
              {"id":1,"name":"Activity","type":"Feature Layer"}
            ],"tables":[]}
            """;
        var store = new ArcGisRestStore(
            new HttpClient(new FixtureHandler(url, Path.Combine(FixtureRoot, directory), serviceRoot)),
            new ArcGisRestServiceOptions { Name = "remote", Url = url });

        var datasets = await store.ListAsync();

        Assert.Equal(["arcgis.l1"], datasets.Select(dataset => dataset.Id));
    }

    [Fact]
    public async Task A_wkid_outside_the_curated_map_leaves_geometry_without_a_crs()
    {
        // WKID 104145/latestWkid 6318 (NAD83(2011)) is real but not in the
        // curated WkidMap, so the layer SRID resolves to 0 and the decoded
        // geometry carries no CoordinateReference. Documented gap, not a
        // silent mis-projection.
        const string directory = "monumentation-monumentation-featureserver";
        const string url = "https://mapservices.nps.gov/arcgis/rest/services/Monumentation/Monumentation/FeatureServer";
        var store = Store(url, directory);

        var description = await store.DescribeAsync("arcgis.l0");
        var batches = await store.ScanAsync("arcgis.l0");

        Assert.Equal(0, description.Srid);
        var geometry = batches.SelectMany(batch => batch.Features).First()["geometry"].GeometryValue!;
        Assert.Null(geometry.CoordinateReference);
    }

    [Fact]
    public async Task A_token_required_error_maps_to_store_unavailable()
    {
        var handler = new FixtureHandler(
            "https://example.invalid/FeatureServer",
            FixtureRoot,
            serviceRoot: """{"error":{"code":499,"message":"Token Required","messageCode":"SB_0006","details":["Token Required"]}}""");
        var store = new ArcGisRestStore(new HttpClient(handler), new ArcGisRestServiceOptions { Name = "remote", Url = "https://example.invalid/FeatureServer" });

        var exception = await Assert.ThrowsAsync<SpatialException>(() => store.ListAsync());

        Assert.Equal(SpatialException.StoreUnavailable, exception.Code);
        Assert.Contains("Token Required", exception.Message, StringComparison.Ordinal);
    }

    private static ArcGisRestStore Store(string url, string directory) =>
        new(new HttpClient(new FixtureHandler(url, Path.Combine(FixtureRoot, directory))),
            new ArcGisRestServiceOptions { Name = "remote", Url = url });

    private static void AssertFieldMapping(JsonElement layer, FeatureSchema schema)
    {
        if (!layer.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var field in fields.EnumerateArray())
        {
            var name = field.GetProperty("name").GetString()!;
            var esriType = field.TryGetProperty("type", out var type) ? type.GetString() : null;
            if (!Spatial.Interop.Esri.EsriFieldType.TryToAttributeKind(esriType, out var kind))
            {
                continue;
            }

            if (kind == AttributeKind.Geometry)
            {
                Assert.DoesNotContain(schema.Fields, candidate => candidate.Name == name && candidate.Kind == AttributeKind.Geometry);
                continue;
            }

            var mapped = Assert.Single(schema.Fields, candidate => candidate.Name == name);
            Assert.Equal(kind, mapped.Kind);
        }
    }

    private static string ExpectedObjectIdField(JsonElement layer)
    {
        if (layer.TryGetProperty("objectIdField", out var oid)
            && oid.ValueKind == JsonValueKind.String
            && oid.GetString() is { Length: > 0 } declared)
        {
            return declared;
        }

        if (layer.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in fields.EnumerateArray())
            {
                if (field.TryGetProperty("type", out var type)
                    && type.GetString() == Spatial.Interop.Esri.EsriFieldType.Oid
                    && field.TryGetProperty("name", out var name))
                {
                    return name.GetString()!;
                }
            }
        }

        return "OBJECTID";
    }

    private static string ExpectedGeometryType(JsonElement layer)
    {
        var esriType = layer.TryGetProperty("geometryType", out var element) ? element.GetString() : null;
        return esriType switch
        {
            "esriGeometryPoint" => "Point",
            "esriGeometryMultipoint" => "MultiPoint",
            "esriGeometryPolyline" => "LineString",
            "esriGeometryPolygon" => "Polygon",
            _ => "Unknown",
        };
    }

    private static int ExpectedSrid(JsonElement layer)
    {
        if (TryWkid(layer, "spatialReference", out var epsg))
        {
            return epsg;
        }

        if (layer.TryGetProperty("extent", out var extent) && TryWkid(extent, "spatialReference", out var extentEpsg))
        {
            return extentEpsg;
        }

        return 0;
    }

    private static bool TryWkid(JsonElement owner, string property, out int epsg)
    {
        epsg = 0;
        if (!owner.TryGetProperty(property, out var reference) || reference.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var name in new[] { "wkid", "latestWkid" })
        {
            if (reference.TryGetProperty(name, out var element)
                && element.ValueKind == JsonValueKind.Number
                && element.TryGetInt32(out var wkid)
                && Spatial.Interop.Esri.WkidMap.TryToEpsg(wkid, out epsg))
            {
                return true;
            }
        }

        return false;
    }

    private static Envelope? ReadEnvelope(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetInt32() : 200;
        var body = root.GetProperty("body").GetRawText();
        return new Envelope(status, body);
    }

    private sealed record Envelope(int Status, string Body);

    /// <summary>
    /// Replays the recorded responses for one service. The store's pagination
    /// loop is satisfied by returning an empty page for any non-zero
    /// <c>resultOffset</c>.
    /// </summary>
    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly string _basePath;
        private readonly string _directory;
        private readonly string? _serviceRoot;

        public FixtureHandler(string baseUrl, string directory, string? serviceRoot = null)
        {
            _basePath = new Uri(baseUrl).AbsolutePath.TrimEnd('/');
            _directory = directory;
            _serviceRoot = serviceRoot;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath.TrimEnd('/');
            if (path == _basePath)
            {
                return Task.FromResult(_serviceRoot is not null
                    ? Json(_serviceRoot, HttpStatusCode.OK)
                    : Read("service.json"));
            }

            var tail = path[(_basePath.Length + 1)..];
            var segments = tail.Split('/');
            if (segments.Length >= 2 && segments[1] == "query")
            {
                if (ResultOffset(uri.Query) > 0)
                {
                    return Task.FromResult(Json("""{"features":[],"exceededTransferLimit":false}""", HttpStatusCode.OK));
                }

                return Task.FromResult(Read($"query-{segments[0]}.json"));
            }

            return Task.FromResult(Read($"layer-{segments[0]}.json"));
        }

        private static int ResultOffset(string query)
        {
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && Uri.UnescapeDataString(parts[0]) == "resultOffset"
                    && int.TryParse(Uri.UnescapeDataString(parts[1]), CultureInfo.InvariantCulture, out var offset))
                {
                    return offset;
                }
            }

            return 0;
        }

        private HttpResponseMessage Read(string file)
        {
            var path = Path.Combine(_directory, file);
            if (!File.Exists(path))
            {
                return Json("""{"error":{"code":404,"message":"Not Found","details":[]}}""", HttpStatusCode.NotFound);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetInt32() : 200;
            return Json(root.GetProperty("body").GetRawText(), (HttpStatusCode)status);
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
