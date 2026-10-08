using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Maps;

namespace Spatial.Maps.Tests;

/// <summary>
/// The declared-map parsing in <see cref="MapRegistry"/>: legacy service
/// names still parse, unknown services and cardinalities fail fast with
/// <c>invalid.arguments</c>, and every cardinality — with and without a join
/// dataset — seeds through the constructor.
/// </summary>
public sealed class MapRegistryParsingTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "spatial-maps-parsing-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string FilePath => Path.Combine(_directory, "maps.json");

    private static DeclaredMapOptions Declared(
        string name,
        IReadOnlyList<string> services,
        params DeclaredLayerOptions[] layers) =>
        new() { Name = name, Store = "memory", Services = services, Layers = layers };

    private MapRegistry Registry(params DeclaredMapOptions[] declared) =>
        new(new MapsOptions { Path = FilePath, Declared = declared }, (_, _) => Task.FromResult<IReadOnlyList<Contracts.Providers.DatasetSummary>>([]));

    private static DeclaredLayerOptions Layer(
        string dataset,
        int layerId,
        DeclaredRelationshipOptions[]? relationships = null,
        string kind = "Feature") =>
        new() { Dataset = dataset, LayerId = layerId, Kind = kind, Relationships = relationships ?? [] };

    private static DeclaredRelationshipOptions Relationship(string cardinality, DeclaredRelationshipJoinOptions? join = null) =>
        new()
        {
            Name = "rel",
            RelatedLayerId = 1,
            PrimaryKeyColumn = "id",
            RelatedKeyColumn = "parent_id",
            Cardinality = cardinality,
            Join = join,
        };

    [Theory]
    [InlineData("feature", MapServiceKind.FeatureServer, "Feature")]
    [InlineData("map", MapServiceKind.MapServer, "Feature")]
    [InlineData("image", MapServiceKind.ImageServer, "Image")]
    [InlineData("FeatureServer", MapServiceKind.FeatureServer, "Feature")]
    [InlineData("Tiles", MapServiceKind.Tiles, "Feature")]
    public async Task Legacy_and_current_service_names_parse(string name, MapServiceKind expected, string layerKind)
    {
        using var registry = Registry(Declared("declared", [name], Layer("memory.roads", 0, kind: layerKind)));
        var map = await registry.GetAsync("declared");
        Assert.Equal([expected], map.Services);
    }

    [Fact]
    public void An_unknown_service_fails_fast()
    {
        var failure = Assert.Throws<SpatialException>(
            () => Registry(Declared("declared", ["carrier-pigeon"], Layer("memory.roads", 0))));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("unknown service 'carrier-pigeon'", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OneToOne", LayerRelationshipCardinality.OneToOne)]
    [InlineData("oneToMany", LayerRelationshipCardinality.OneToMany)]
    [InlineData("ManyToMany", LayerRelationshipCardinality.ManyToMany)]
    public async Task Every_cardinality_seeds(string cardinality, LayerRelationshipCardinality expected)
    {
        DeclaredRelationshipOptions relationship = cardinality == "ManyToMany"
            ? Relationship(cardinality, new DeclaredRelationshipJoinOptions
            {
                Dataset = "memory.links",
                PrimaryKeyColumn = "origin_id",
                RelatedKeyColumn = "related_id",
            })
            : Relationship(cardinality);
        using var registry = Registry(Declared(
            "declared",
            ["FeatureServer"],
            Layer("memory.parents", 0, [relationship]),
            Layer("memory.children", 1)));

        var map = await registry.GetAsync("declared");

        var seeded = Assert.Single(map.Layers[0].Relationships!);
        Assert.Equal(expected, seeded.Cardinality);
    }

    [Fact]
    public void An_unknown_cardinality_fails_fast()
    {
        var failure = Assert.Throws<SpatialException>(() => Registry(Declared(
            "declared",
            ["FeatureServer"],
            Layer("memory.parents", 0, [Relationship("one-to-everywhere")]),
            Layer("memory.children", 1))));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("unknown cardinality 'one-to-everywhere'", failure.Message, StringComparison.Ordinal);
    }
}
