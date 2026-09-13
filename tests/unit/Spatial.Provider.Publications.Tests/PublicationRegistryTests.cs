using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;
using Spatial.Provider.Publications;

namespace Spatial.Provider.Publications.Tests;

/// <summary>
/// The publication registry (ADR-0041 §2): declared immutability, whole-store
/// expansion, runtime persistence, atomic replace, collisions and corrupt-file
/// diagnostics. The file lives under a per-test temporary directory.
/// </summary>
public sealed class PublicationRegistryTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "spatial-publications-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string FilePath => System.IO.Path.Combine(_directory, "publications.json");

    private PublicationRegistry Registry(PublicationsOptions? options = null, PublicationRegistry.DatasetEnumerator? enumerate = null)
    {
        options ??= new PublicationsOptions { Path = FilePath };
        options.Path = FilePath;
        return new PublicationRegistry(
            options,
            enumerate ?? ((_, _) => Task.FromResult<IReadOnlyList<DatasetSummary>>([])));
    }

    private static Publication Feature(string name, params PublicationLayer[] layers) =>
        new(name, PublicationKind.Feature, "memory", layers);

    [Fact]
    public async Task A_runtime_publication_round_trips_through_the_file()
    {
        using (var registry = Registry())
        {
            var stored = await registry.PutAsync(Feature("parks", new PublicationLayer("memory.parks", 0)));
            Assert.Equal("parks", stored.Name);

            var fetched = await registry.GetAsync("parks");
            Assert.Equal(["memory.parks"], fetched.Layers.Select(layer => layer.Dataset));
        }

        using var reopened = Registry();
        var reloaded = await reopened.GetAsync("parks");
        Assert.Equal(PublicationKind.Feature, reloaded.Kind);
        Assert.Single(reloaded.Layers);
    }

    [Fact]
    public async Task A_layer_style_round_trips_through_the_file()
    {
        const string style = """[{"type":"fill","paint":{"fill-color":"#ff0000","fill-opacity":0.5}}]""";
        using (var registry = Registry())
        {
            await registry.PutAsync(Feature("parks", new PublicationLayer("memory.parks", 0, null, style)));
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
            await registry.PutAsync(Feature("parks", new PublicationLayer("memory.parks", 0)));
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
            registry.PutAsync(Feature("parks", new PublicationLayer("memory.parks", 0, null, style))));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task A_declared_publication_is_immutable_and_listed_first()
    {
        var options = new PublicationsOptions
        {
            Path = FilePath,
            Declared =
            [
                new DeclaredPublicationOptions
                {
                    Name = "demo",
                    Store = "demo",
                    Layers = [new DeclaredLayerOptions { Dataset = "demo.points", LayerId = 0 }],
                },
            ],
        };
        using var registry = Registry(options);
        await registry.PutAsync(Feature("zebra", new PublicationLayer("memory.zebra", 0)));

        var listed = await registry.ListAsync();
        Assert.Equal(["demo", "zebra"], listed.Select(publication => publication.Name));

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            registry.PutAsync(Feature("demo", new PublicationLayer("memory.x", 0))));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.False(await registry.DeleteAsync("demo"));
    }

    [Fact]
    public async Task A_whole_store_declared_publication_expands_and_caches_its_layers()
    {
        var calls = 0;
        var options = new PublicationsOptions
        {
            Path = FilePath,
            Declared = [new DeclaredPublicationOptions { Name = "demo", Store = "demo" }],
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
    public async Task Replacing_a_runtime_publication_appends_new_layer_ids()
    {
        using var registry = Registry();
        await registry.PutAsync(Feature("parks", new PublicationLayer("memory.parks", 0)));

        var replaced = await registry.PutAsync(Feature("parks", new PublicationLayer("memory.parks", 0), new PublicationLayer("memory.trees", -1)));

        Assert.Equal([0, 1], replaced.Layers.Select(layer => layer.LayerId));
    }

    [Fact]
    public async Task Duplicate_layer_ids_are_rejected()
    {
        using var registry = Registry();

        var failure = await Assert.ThrowsAsync<SpatialException>(() => registry.PutAsync(
            Feature("parks", new PublicationLayer("memory.a", 0), new PublicationLayer("memory.b", 0))));

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
            registry.PutAsync(Feature(name, new PublicationLayer("memory.a", 0))));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task A_missing_runtime_publication_is_not_found()
    {
        using var registry = Registry();

        var failure = await Assert.ThrowsAsync<SpatialException>(() => registry.GetAsync("nope"));

        Assert.Equal(SpatialException.NotFound, failure.Code);
    }

    [Fact]
    public async Task Delete_reports_whether_the_publication_existed()
    {
        using var registry = Registry();
        await registry.PutAsync(Feature("parks", new PublicationLayer("memory.parks", 0)));

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
    public async Task The_file_carries_a_schema_version()
    {
        using (var registry = Registry())
        {
            await registry.PutAsync(Feature("parks", new PublicationLayer("memory.parks", 0)));
        }

        Assert.Contains("\"version\": 1", await File.ReadAllTextAsync(FilePath));
    }
}
