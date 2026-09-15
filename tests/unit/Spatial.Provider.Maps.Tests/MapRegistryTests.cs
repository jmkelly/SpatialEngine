using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;
using Spatial.Provider.Maps;

namespace Spatial.Provider.Maps.Tests;

/// <summary>
/// The map registry (ADR-0053 §2): declared immutability, whole-store
/// expansion, runtime persistence, atomic replace, collisions, corrupt-file
/// diagnostics and legacy map migration. The file lives under a
/// per-test temporary directory.
/// </summary>
public sealed class MapRegistryTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "spatial-maps-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string FilePath => System.IO.Path.Combine(_directory, "maps.json");

    private MapRegistry Registry(MapsOptions? options = null, MapRegistry.DatasetEnumerator? enumerate = null)
    {
        options ??= new MapsOptions { Path = FilePath };
        options.Path = FilePath;
        return new MapRegistry(
            options,
            enumerate ?? ((_, _) => Task.FromResult<IReadOnlyList<DatasetSummary>>([])));
    }

    private static Map Feature(string name, params MapLayer[] layers) =>
        new(name, "memory", layers, [MapService.Feature]);

    [Fact]
    public async Task A_runtime_map_round_trips_through_the_file()
    {
        using (var registry = Registry())
        {
            var stored = await registry.PutAsync(Feature("parks", new MapLayer("memory.parks", 0)));
            Assert.Equal("parks", stored.Name);

            var fetched = await registry.GetAsync("parks");
            Assert.Equal(["memory.parks"], fetched.Layers.Select(layer => layer.Dataset));
        }

        using var reopened = Registry();
        var reloaded = await reopened.GetAsync("parks");
        Assert.Equal([MapService.Feature], reloaded.Services);
        Assert.Single(reloaded.Layers);
    }

    [Fact]
    public async Task A_layer_style_round_trips_through_the_file()
    {
        const string style = """[{"type":"fill","paint":{"fill-color":"#ff0000","fill-opacity":0.5}}]""";
        using (var registry = Registry())
        {
            await registry.PutAsync(Feature("parks", new MapLayer("memory.parks", 0, null, style)));
        }

        using var reopened = Registry();
        var reloaded = await reopened.GetAsync("parks");
        Assert.Equal(style, reloaded.Layers[0].Style);
    }

    [Fact]
    public async Task A_layer_without_style_keeps_a_null_style()
    {
        using (var registry = Registry())
        {
            await registry.PutAsync(Feature("parks", new MapLayer("memory.parks", 0)));
        }

        using var reopened = Registry();
        Assert.Null((await reopened.GetAsync("parks")).Layers[0].Style);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[1, 2]")]
    public async Task A_malformed_layer_style_is_rejected(string style)
    {
        using var registry = Registry();

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            registry.PutAsync(Feature("parks", new MapLayer("memory.parks", 0, null, style))));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task A_map_exposes_several_services()
    {
        using var registry = Registry();

        var stored = await registry.PutAsync(new Map(
            "mixed",
            "memory",
            [new MapLayer("memory.parks", 0)],
            [MapService.Feature, MapService.Map, MapService.Tiles, MapService.Wms, MapService.Wfs]));

        Assert.Equal(
            [MapService.Feature, MapService.Map, MapService.Tiles, MapService.Wms, MapService.Wfs],
            stored.Services);
        Assert.True(stored.Exposes(MapService.Wms));
        Assert.False(stored.Exposes(MapService.Image));
    }

    [Fact]
    public async Task A_vector_service_without_a_feature_layer_is_rejected()
    {
        using var registry = Registry();

        var failure = await Assert.ThrowsAsync<SpatialException>(() => registry.PutAsync(new Map(
            "raster_only", "raster", [new MapLayer("ortho", 0, null, null, MapLayerKind.Image)], [MapService.Map])));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task An_image_service_without_an_image_layer_is_rejected()
    {
        using var registry = Registry();

        var failure = await Assert.ThrowsAsync<SpatialException>(() => registry.PutAsync(new Map(
            "vector", "memory", [new MapLayer("memory.parks", 0)], [MapService.Image])));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task A_declared_map_is_immutable_and_listed_first()
    {
        var options = new MapsOptions
        {
            Path = FilePath,
            Declared =
            [
                new DeclaredMapOptions
                {
                    Name = "demo",
                    Store = "demo",
                    Services = [nameof(MapService.Feature)],
                    Layers = [new DeclaredLayerOptions { Dataset = "demo.points", LayerId = 0 }],
                },
            ],
        };
        using var registry = Registry(options);
        await registry.PutAsync(Feature("zebra", new MapLayer("memory.zebra", 0)));

        var listed = await registry.ListAsync();
        Assert.Equal(["demo", "zebra"], listed.Select(map => map.Name));

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            registry.PutAsync(Feature("demo", new MapLayer("memory.x", 0))));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.False(await registry.DeleteAsync("demo"));
    }

    [Fact]
    public async Task A_whole_store_declared_map_expands_and_caches_its_layers()
    {
        var calls = 0;
        var options = new MapsOptions
        {
            Path = FilePath,
            Declared = [new DeclaredMapOptions { Name = "demo", Store = "demo", Services = [nameof(MapService.Feature)] }],
        };
        using var registry = Registry(options, (store, _) =>
        {
            calls++;
            Assert.Equal("demo", store);
            IReadOnlyList<DatasetSummary> datasets =
            [
                new("demo.cities", "demo", "cities", "geometry", 4326, 8),
                new("demo.points", "demo", "points", "geometry", 4326, 110),
            ];
            return Task.FromResult(datasets);
        });

        var first = await registry.GetAsync("demo");
        var second = await registry.GetAsync("demo");

        Assert.Equal(["demo.cities", "demo.points"], first.Layers.Select(layer => layer.Dataset));
        Assert.Equal([0, 1], first.Layers.Select(layer => layer.LayerId));
        Assert.Equal(1, calls);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task Replacing_a_runtime_map_appends_new_layer_ids()
    {
        using var registry = Registry();
        await registry.PutAsync(Feature("parks", new MapLayer("memory.parks", 0)));

        var replaced = await registry.PutAsync(Feature("parks", new MapLayer("memory.parks", 0), new MapLayer("memory.trees", -1)));

        Assert.Equal([0, 1], replaced.Layers.Select(layer => layer.LayerId));
    }

    [Fact]
    public async Task Duplicate_layer_ids_are_rejected()
    {
        using var registry = Registry();

        var failure = await Assert.ThrowsAsync<SpatialException>(() => registry.PutAsync(
            Feature("parks", new MapLayer("memory.a", 0), new MapLayer("memory.b", 0))));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Theory]
    [InlineData("Folder/Service")]
    [InlineData("has space")]
    [InlineData("")]
    public async Task An_invalid_name_is_rejected(string name)
    {
        using var registry = Registry();

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            registry.PutAsync(Feature(name, new MapLayer("memory.a", 0))));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task A_missing_runtime_map_is_not_found()
    {
        using var registry = Registry();

        var failure = await Assert.ThrowsAsync<SpatialException>(() => registry.GetAsync("nope"));

        Assert.Equal(SpatialException.NotFound, failure.Code);
    }

    [Fact]
    public async Task Delete_reports_whether_the_map_existed()
    {
        using var registry = Registry();
        await registry.PutAsync(Feature("parks", new MapLayer("memory.parks", 0)));

        Assert.True(await registry.DeleteAsync("parks"));
        Assert.False(await registry.DeleteAsync("parks"));
    }

    [Fact]
    public async Task A_corrupt_file_is_a_store_unavailable_diagnostic()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(FilePath, "{ not json");
        using var registry = Registry();

        var failure = await Assert.ThrowsAsync<SpatialException>(() => registry.ListAsync());

        Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
        Assert.Contains("not valid JSON", failure.Message);
    }

    [Fact]
    public async Task A_legacy_publications_file_is_migrated_on_first_read()
    {
        Directory.CreateDirectory(_directory);
        var legacyPath = System.IO.Path.Combine(_directory, "publications.json");
        await File.WriteAllTextAsync(legacyPath, """
            {
              "version": 1,
              "publications": [
                {
                  "name": "legacy",
                  "kind": "map",
                  "store": "demo",
                  "layers": [{ "dataset": "demo.points", "layerId": 0 }]
                }
              ]
            }
            """);

        using var registry = Registry(new MapsOptions { Path = FilePath, LegacyPath = legacyPath });
        var migrated = await registry.GetAsync("legacy");

        Assert.Equal([MapService.Map], migrated.Services);
        Assert.Equal("demo.points", migrated.Layers[0].Dataset);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task Migrated_legacy_entries_are_persisted_with_the_runtime_file()
    {
        Directory.CreateDirectory(_directory);
        var legacyPath = System.IO.Path.Combine(_directory, "publications.json");
        await File.WriteAllTextAsync(legacyPath, """{"version":1,"publications":[{"name":"legacy","kind":"feature","store":"demo","layers":[{"dataset":"demo.points","layerId":0}]}]}""");
        using (var registry = Registry(new MapsOptions { Path = FilePath, LegacyPath = legacyPath }))
        {
            await registry.PutAsync(Feature("modern", new MapLayer("memory.a", 0)));
        }

        // The first write persists the migrated entry alongside the runtime one,
        // so a later reopen reads the new file and never touches the legacy file.
        using var reopened = Registry(new MapsOptions { Path = FilePath, LegacyPath = legacyPath });
        Assert.Equal(["legacy", "modern"], (await reopened.ListAsync()).Select(map => map.Name));
    }

    [Fact]
    public async Task The_file_carries_a_schema_version()
    {
        using (var registry = Registry())
        {
            await registry.PutAsync(Feature("parks", new MapLayer("memory.parks", 0)));
        }

        Assert.Contains("\"version\": 1", await File.ReadAllTextAsync(FilePath));
    }

    [Theory]
    [InlineData("feature", MapService.Feature)]
    [InlineData("map", MapService.Map)]
    [InlineData("image", MapService.Image)]
    public async Task A_legacy_kind_maps_to_its_service(string kind, MapService expected)
    {
        Directory.CreateDirectory(_directory);
        var legacyPath = System.IO.Path.Combine(_directory, "publications.json");
        var json = "{\"version\":1,\"publications\":[{\"name\":\"legacy\",\"kind\":\"" + kind + "\",\"store\":\"demo\",\"layers\":[{\"dataset\":\"raster.ortho\",\"layerId\":0}]}]}";
        await File.WriteAllTextAsync(legacyPath, json);

        using var registry = Registry(new MapsOptions { Path = FilePath, LegacyPath = legacyPath });
        var migrated = await registry.GetAsync("legacy");

        Assert.Equal([expected], migrated.Services);
        var expectedKind = expected == MapService.Image ? MapLayerKind.Image : MapLayerKind.Feature;
        Assert.Equal(expectedKind, migrated.Layers[0].Kind);
    }

    [Fact]
    public async Task Legacy_entries_without_layers_or_unknown_kinds_are_skipped()
    {
        Directory.CreateDirectory(_directory);
        var legacyPath = System.IO.Path.Combine(_directory, "publications.json");
        await File.WriteAllTextAsync(legacyPath, """
            {"version":1,"publications":[
              {"name":"empty","kind":"feature","store":"demo","layers":[]},
              {"name":"weird","kind":"table","store":"demo","layers":[{"dataset":"demo.points","layerId":0}]},
              {"name":"kept","kind":"feature","store":"demo","layers":[{"dataset":"demo.points","layerId":0}]}
            ]}
            """);

        using var registry = Registry(new MapsOptions { Path = FilePath, LegacyPath = legacyPath });

        Assert.Equal(["kept"], (await registry.ListAsync()).Select(map => map.Name));
    }

    [Fact]
    public async Task A_corrupt_legacy_file_is_a_store_unavailable_diagnostic()
    {
        Directory.CreateDirectory(_directory);
        var legacyPath = System.IO.Path.Combine(_directory, "publications.json");
        await File.WriteAllTextAsync(legacyPath, "{ not json");
        using var registry = Registry(new MapsOptions { Path = FilePath, LegacyPath = legacyPath });

        var failure = await Assert.ThrowsAsync<SpatialException>(() => registry.ListAsync());

        Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
        Assert.Contains("legacy map file", failure.Message);
    }

    [Fact]
    public async Task A_declared_map_can_carry_an_image_layer_and_a_service_set()
    {
        var options = new MapsOptions
        {
            Path = FilePath,
            Declared =
            [
                new DeclaredMapOptions
                {
                    Name = "imagery",
                    Store = "raster",
                    Services = [nameof(MapService.Image), nameof(MapService.Wms)],
                    Layers =
                    [
                        new DeclaredLayerOptions { Dataset = "ortho", LayerId = 0, Kind = nameof(MapLayerKind.Image) },
                        new DeclaredLayerOptions { Dataset = "demo.points", LayerId = 1, Kind = nameof(MapLayerKind.Feature) },
                    ],
                },
            ],
        };

        using var registry = Registry(options);
        var map = await registry.GetAsync("imagery");

        Assert.Equal([MapService.Image, MapService.Wms], map.Services);
        Assert.Equal([MapLayerKind.Image, MapLayerKind.Feature], map.Layers.Select(layer => layer.Kind));
    }

    [Fact]
    public void A_declared_map_with_an_unknown_service_is_rejected()
    {
        var options = new MapsOptions
        {
            Path = FilePath,
            Declared = [new DeclaredMapOptions { Name = "bad", Store = "demo", Services = ["teleport"], Layers = [new DeclaredLayerOptions { Dataset = "demo.points", LayerId = 0 }] }],
        };

        var failure = Assert.Throws<SpatialException>(() => Registry(options));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public void A_declared_map_with_an_unknown_layer_kind_is_rejected()
    {
        var options = new MapsOptions
        {
            Path = FilePath,
            Declared = [new DeclaredMapOptions { Name = "bad", Store = "demo", Services = [nameof(MapService.Feature)], Layers = [new DeclaredLayerOptions { Dataset = "demo.points", LayerId = 0, Kind = "hologram" }] }],
        };

        var failure = Assert.Throws<SpatialException>(() => Registry(options));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task An_image_map_round_trips_its_authored_service_metadata()
    {
        const string xml = "<MD_Metadata><title>Fixture</title></MD_Metadata>";
        using var registry = Registry();

        var stored = await registry.PutAsync(new Map(
            "ortho",
            "raster",
            [new MapLayer("ortho", 0, null, null, MapLayerKind.Image)],
            [MapService.Image],
            MetadataXml: xml));

        Assert.Equal(xml, stored.MetadataXml);
        Assert.Equal(xml, (await registry.GetAsync("ortho")).MetadataXml);

        using var reopened = Registry();
        Assert.Equal(xml, (await reopened.GetAsync("ortho")).MetadataXml);
    }

    [Fact]
    public async Task Malformed_service_metadata_is_rejected()
    {
        using var registry = Registry();

        var failure = await Assert.ThrowsAsync<SpatialException>(() => registry.PutAsync(new Map(
            "ortho",
            "raster",
            [new MapLayer("ortho", 0, null, null, MapLayerKind.Image)],
            [MapService.Image],
            MetadataXml: "<MD_Metadata><title>unclosed")));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task A_declared_map_carries_its_authored_service_metadata()
    {
        const string xml = "<MD_Metadata><title>Declared</title></MD_Metadata>";
        var options = new MapsOptions
        {
            Path = FilePath,
            Declared =
            [
                new DeclaredMapOptions
                {
                    Name = "ortho",
                    Store = "raster",
                    Services = [nameof(MapService.Image)],
                    MetadataXml = xml,
                    Layers = [new DeclaredLayerOptions { Dataset = "ortho", LayerId = 0, Kind = nameof(MapLayerKind.Image) }],
                },
            ],
        };

        using var registry = Registry(options);
        Assert.Equal(xml, (await registry.GetAsync("ortho")).MetadataXml);
    }

    [Fact]
    public void A_declared_map_with_malformed_service_metadata_is_rejected()
    {
        var options = new MapsOptions
        {
            Path = FilePath,
            Declared =
            [
                new DeclaredMapOptions
                {
                    Name = "ortho",
                    Store = "raster",
                    Services = [nameof(MapService.Image)],
                    MetadataXml = "<MD_Metadata><title>unclosed",
                    Layers = [new DeclaredLayerOptions { Dataset = "ortho", LayerId = 0, Kind = nameof(MapLayerKind.Image) }],
                },
            ],
        };

        var failure = Assert.Throws<SpatialException>(() => Registry(options));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }
}
