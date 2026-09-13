using Spatial.Cli;
using Spatial.Client;
using Spatial.PluginSdk.Providers;

namespace Spatial.Cli.Tests;

/// <summary>
/// The mutating <c>map</c> commands (ADR-0052): create/delete and the
/// add/remove/style layer verbs, including append-only layer ids (ADR-0041),
/// the admin-token guard and dry-run planning.
/// </summary>
public sealed class MapCommandTests
{
    [Fact]
    public async Task Create_builds_the_publication_and_passes_the_token()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "map", "create",
            "--name", "World",
            "--kind", "map",
            "--store", "memory",
            "--description", "A world map",
            "--copyright", "Natural Earth",
            "--layer", "public.world=Countries",
            "--layer", "public.roads",
            "--token", "secret");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var (publication, token) = Assert.Single(gateway.PutCalls);
        Assert.Equal("secret", token);
        Assert.Equal("World", publication.Name);
        Assert.Equal(PublicationKind.Map, publication.Kind);
        Assert.Equal("memory", publication.Store);
        Assert.Equal("A world map", publication.Description);
        Assert.Equal("Natural Earth", publication.Copyright);
        Assert.Collection(
            publication.Layers,
            layer =>
            {
                Assert.Equal("public.world", layer.Dataset);
                Assert.Equal(0, layer.LayerId);
                Assert.Equal("Countries", layer.Name);
            },
            layer =>
            {
                Assert.Equal("public.roads", layer.Dataset);
                Assert.Equal(1, layer.LayerId);
                Assert.Null(layer.Name);
            });
    }

    [Fact]
    public async Task Create_without_force_on_an_existing_publication_is_a_usage_error()
    {
        var gateway = new FakeSpatialGateway
        {
            PublicationsByName = new Dictionary<string, Publication>(StringComparer.Ordinal)
            {
                ["World"] = new Publication("World", PublicationKind.Map, "memory", []),
            },
        };

        var run = await CliHarness.RunAsync(
            gateway,
            "map", "create",
            "--name", "World",
            "--kind", "map",
            "--token", "secret");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("--force", run.Error, StringComparison.Ordinal);
        Assert.Empty(gateway.PutCalls);
    }

    [Fact]
    public async Task Create_requires_an_admin_token()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "map", "create",
            "--name", "World",
            "--kind", "feature");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("Admin token required", run.Error, StringComparison.Ordinal);
        Assert.Empty(gateway.PutCalls);
    }

    [Fact]
    public async Task Create_force_reuses_existing_layer_ids_and_assigns_next_free()
    {
        var gateway = new FakeSpatialGateway
        {
            PublicationsByName = new Dictionary<string, Publication>(StringComparer.Ordinal)
            {
                ["World"] = new Publication(
                    "World",
                    PublicationKind.Map,
                    "memory",
                    [new PublicationLayer("public.world", 3), new PublicationLayer("public.roads", 7)]),
            },
        };

        var run = await CliHarness.RunAsync(
            gateway,
            "map", "create",
            "--name", "World",
            "--kind", "map",
            "--force",
            "--layer", "public.roads",
            "--layer", "public.rivers",
            "--token", "secret");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var (publication, _) = Assert.Single(gateway.PutCalls);
        Assert.Collection(
            publication.Layers,
            layer => Assert.Equal(("public.roads", 7), (layer.Dataset, layer.LayerId)),
            layer => Assert.Equal(("public.rivers", 8), (layer.Dataset, layer.LayerId)));
    }

    [Fact]
    public async Task Create_rejects_a_layer_dataset_listed_twice()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "map", "create",
            "--name", "World",
            "--kind", "map",
            "--layer", "public.world",
            "--layer", "public.world=Other",
            "--token", "secret");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("Duplicate layer dataset", run.Error, StringComparison.Ordinal);
        Assert.Empty(gateway.PutCalls);
    }

    [Fact]
    public async Task Add_layer_appends_with_the_next_free_id()
    {
        var gateway = new FakeSpatialGateway
        {
            PublicationsByName = new Dictionary<string, Publication>(StringComparer.Ordinal)
            {
                ["World"] = new Publication(
                    "World",
                    PublicationKind.Map,
                    "memory",
                    [new PublicationLayer("public.world", 2), new PublicationLayer("public.roads", 5)]),
            },
        };

        var run = await CliHarness.RunAsync(
            gateway,
            "map", "add-layer",
            "--map", "World",
            "--dataset", "public.rivers",
            "--name", "Rivers",
            "--token", "secret");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var (publication, _) = Assert.Single(gateway.PutCalls);
        Assert.Equal(3, publication.Layers.Count);
        var added = publication.Layers[^1];
        Assert.Equal(("public.rivers", 6, "Rivers"), (added.Dataset, added.LayerId, added.Name));
    }

    [Fact]
    public async Task Add_layer_rejects_a_dataset_already_present()
    {
        var gateway = new FakeSpatialGateway
        {
            PublicationsByName = new Dictionary<string, Publication>(StringComparer.Ordinal)
            {
                ["World"] = new Publication("World", PublicationKind.Map, "memory", [new PublicationLayer("public.world", 0)]),
            },
        };

        var run = await CliHarness.RunAsync(
            gateway,
            "map", "add-layer",
            "--map", "World",
            "--dataset", "public.world",
            "--token", "secret");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Empty(gateway.PutCalls);
    }

    [Fact]
    public async Task Add_layer_reports_not_found_for_an_unknown_publication()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "map", "add-layer",
            "--map", "Missing",
            "--dataset", "public.world",
            "--token", "secret");

        Assert.Equal(ExitCodes.NotFound, run.ExitCode);
        Assert.Contains("not.found", run.Error, StringComparison.Ordinal);
        Assert.Empty(gateway.PutCalls);
    }

    [Fact]
    public async Task Remove_layer_drops_the_dataset_and_keeps_the_other_ids()
    {
        var gateway = new FakeSpatialGateway
        {
            PublicationsByName = new Dictionary<string, Publication>(StringComparer.Ordinal)
            {
                ["World"] = new Publication(
                    "World",
                    PublicationKind.Map,
                    "memory",
                    [new PublicationLayer("public.world", 0), new PublicationLayer("public.roads", 5), new PublicationLayer("public.rivers", 9)]),
            },
        };

        var run = await CliHarness.RunAsync(
            gateway,
            "map", "remove-layer",
            "--map", "World",
            "--dataset", "public.roads",
            "--token", "secret");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var (publication, _) = Assert.Single(gateway.PutCalls);
        Assert.Collection(
            publication.Layers,
            layer => Assert.Equal(("public.world", 0), (layer.Dataset, layer.LayerId)),
            layer => Assert.Equal(("public.rivers", 9), (layer.Dataset, layer.LayerId)));
    }

    [Fact]
    public async Task Remove_layer_reports_not_found_for_an_unknown_dataset()
    {
        var gateway = new FakeSpatialGateway
        {
            PublicationsByName = new Dictionary<string, Publication>(StringComparer.Ordinal)
            {
                ["World"] = new Publication("World", PublicationKind.Map, "memory", [new PublicationLayer("public.world", 0)]),
            },
        };

        var run = await CliHarness.RunAsync(
            gateway,
            "map", "remove-layer",
            "--map", "World",
            "--dataset", "public.roads",
            "--token", "secret");

        Assert.Equal(ExitCodes.NotFound, run.ExitCode);
        Assert.Contains("not.found", run.Error, StringComparison.Ordinal);
        Assert.Empty(gateway.PutCalls);
    }

    [Fact]
    public async Task Set_style_overrides_only_the_supplied_fields()
    {
        var gateway = new FakeSpatialGateway();
        var initial = new DrawRecipe("#ff0000", 0.5, 4, 9, true);
        gateway.PublicationsByName = new Dictionary<string, Publication>(StringComparer.Ordinal)
        {
            ["World"] = new Publication(
                "World",
                PublicationKind.Map,
                "memory",
                [new PublicationLayer("public.world", 0, "Countries", MapLibreStyleBuilder.Lower(initial, GeometryFamily.Mixed))]),
        };

        var run = await CliHarness.RunAsync(
            gateway,
            "map", "set-style",
            "--map", "World",
            "--dataset", "public.world",
            "--color", "#00ff00",
            "--token", "secret");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var (publication, _) = Assert.Single(gateway.PutCalls);
        var layer = Assert.Single(publication.Layers);
        Assert.Equal(("public.world", 0, "Countries"), (layer.Dataset, layer.LayerId, layer.Name));
        Assert.True(MapLibreStyleBuilder.TryDescribe(layer.Style, out var recipe, out var family));
        Assert.Equal(GeometryFamily.Mixed, family);
        Assert.Equal("#00ff00", recipe.Color);
        Assert.Equal(0.5, recipe.Opacity);
        Assert.Equal(4, recipe.LineWidth);
        Assert.Equal(9, recipe.Radius);
        Assert.True(recipe.Visible);
    }

    [Fact]
    public async Task Set_style_hidden_lowers_a_hidden_fragment()
    {
        var gateway = new FakeSpatialGateway
        {
            PublicationsByName = new Dictionary<string, Publication>(StringComparer.Ordinal)
            {
                ["World"] = new Publication("World", PublicationKind.Map, "memory", [new PublicationLayer("public.world", 0)]),
            },
        };

        var run = await CliHarness.RunAsync(
            gateway,
            "map", "set-style",
            "--map", "World",
            "--dataset", "public.world",
            "--geometry", "point",
            "--hidden",
            "--token", "secret");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var (publication, _) = Assert.Single(gateway.PutCalls);
        Assert.True(MapLibreStyleBuilder.TryDescribe(publication.Layers[0].Style, out var recipe, out var family));
        Assert.Equal(GeometryFamily.Point, family);
        Assert.False(recipe.Visible);
    }

    [Fact]
    public async Task Delete_removes_the_publication()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(gateway, "map", "delete", "World", "--token", "secret");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(["World"], gateway.DeletedPublications);
    }

    [Fact]
    public async Task Delete_reports_not_found_when_the_gateway_returns_false()
    {
        var gateway = new DeleteReturnsFalseGateway();

        var run = await CliHarness.RunAsync(gateway, "map", "delete", "Missing", "--token", "secret");

        Assert.Equal(ExitCodes.NotFound, run.ExitCode);
        Assert.Contains("not.found", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_dry_run_plans_without_mutating()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(
            gateway,
            "--dry-run",
            "map", "create",
            "--name", "World",
            "--kind", "map",
            "--layer", "public.world");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("World", run.Output, StringComparison.Ordinal);
        Assert.Empty(gateway.PutCalls);
    }

    [Fact]
    public async Task Delete_dry_run_plans_without_mutating()
    {
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(gateway, "--dry-run", "map", "delete", "World");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("World", run.Output, StringComparison.Ordinal);
        Assert.Empty(gateway.DeletedPublications);
    }

    /// <summary>An <see cref="ISpatialGateway"/> whose delete reports that nothing existed.</summary>
    private sealed class DeleteReturnsFalseGateway : ISpatialGateway
    {
        private readonly FakeSpatialGateway _inner = new();

        public Task<HostHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            _inner.CheckHealthAsync(cancellationToken);

        public Task<IReadOnlyList<DatasetSummary>> ListDatasetsAsync(string store, string? pattern, CancellationToken cancellationToken = default) =>
            _inner.ListDatasetsAsync(store, pattern, cancellationToken);

        public Task<DatasetDescription> DescribeDatasetAsync(string dataset, string store, CancellationToken cancellationToken = default) =>
            _inner.DescribeDatasetAsync(dataset, store, cancellationToken);

        public Task<IngestOutcome> IngestAsync(string source, IngestUpload upload, string? adminToken, CancellationToken cancellationToken = default) =>
            _inner.IngestAsync(source, upload, adminToken, cancellationToken);

        public Task<IReadOnlyList<Publication>> ListPublicationsAsync(CancellationToken cancellationToken = default) =>
            _inner.ListPublicationsAsync(cancellationToken);

        public Task<Publication?> FindPublicationAsync(string name, CancellationToken cancellationToken = default) =>
            _inner.FindPublicationAsync(name, cancellationToken);

        public Task<Publication> PutPublicationAsync(Publication publication, string? adminToken, CancellationToken cancellationToken = default) =>
            _inner.PutPublicationAsync(publication, adminToken, cancellationToken);

        public Task<bool> DeletePublicationAsync(string name, string? adminToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public void Dispose() => _inner.Dispose();
    }
}
