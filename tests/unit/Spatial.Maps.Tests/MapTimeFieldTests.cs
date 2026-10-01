using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Maps;

namespace Spatial.Maps.Tests;

/// <summary>
/// A published layer's designation of the dates bounding a feature
/// (ADR-0183): the structural rules are pure and live in
/// <see cref="MapValidator"/>, the live facts live in
/// <see cref="MapTimeFieldSchemas"/>, and declared configuration parses into
/// the same <see cref="TemporalExtentFields"/> the API carries.
/// </summary>
public sealed class MapTimeFieldTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "spatial-time-fields-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string FilePath => Path.Combine(_directory, "maps.json");

    private static Map Layer(MapLayer layer) => new("time", "postgis", [layer], [MapServiceKind.MapServer]);

    private static DatasetDescription Description(params FieldDefinition[] fields) =>
        new("public.events", "public", "events", "geom", 4326, "Point", 0, [], new FeatureSchema(fields));

    private static readonly FieldDefinition Name = new("name", AttributeKind.String, nullable: true);

    private static readonly FieldDefinition Starts = new("starts", AttributeKind.DateTimeOffset, nullable: true);

    private static readonly FieldDefinition Ends = new("ends", AttributeKind.DateTimeOffset, nullable: true);

    // ---- structural ----

    [Fact]
    public void A_layer_may_designate_the_dates_that_bound_it()
    {
        var map = Layer(new MapLayer("public.events", 0) { TimeFields = new TemporalExtentFields("starts", "ends") });

        var normalised = MapValidator.Normalize(map, nextLayerId: 0);

        Assert.Equal(new TemporalExtentFields("starts", "ends"), Assert.Single(normalised.Layers).TimeFields);
    }

    [Fact]
    public void A_layer_that_designs_either_bound_alone_designs_that_bound()
    {
        var map = Layer(new MapLayer("public.events", 0) { TimeFields = new TemporalExtentFields("starts", null) });

        var normalised = MapValidator.Normalize(map, nextLayerId: 0);

        Assert.Equal(new TemporalExtentFields("starts", null), Assert.Single(normalised.Layers).TimeFields);
    }

    [Fact]
    public void A_field_name_that_is_not_an_identifier_is_rejected()
    {
        var map = Layer(new MapLayer("public.events", 0) { TimeFields = new TemporalExtentFields("starts", "ends) or 1=1") });

        var failure = Assert.Throws<SpatialException>(() => MapValidator.Normalize(map, nextLayerId: 0));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("ends) or 1=1", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_layer_may_not_designate_dates()
    {
        var map = new Map(
            "ortho",
            "raster",
            [new MapLayer("ortho.tif", 0, Kind: MapLayerKind.Image) { TimeFields = new TemporalExtentFields("starts", "ends") }],
            [MapServiceKind.ImageServer]);

        var failure = Assert.Throws<SpatialException>(() => MapValidator.Normalize(map, nextLayerId: 0));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("image layer", failure.Message, StringComparison.Ordinal);
    }

    // ---- live schema ----

    [Fact]
    public async Task A_designation_over_fields_the_dataset_has_is_accepted()
    {
        var map = Layer(new MapLayer("public.events", 0) { TimeFields = new TemporalExtentFields("starts", "ends") });

        await MapTimeFieldSchemas.ValidateAsync(map, (_, _, _) => Task.FromResult(Description(Name, Starts, Ends)));
    }

    [Fact]
    public async Task A_designation_over_a_field_the_dataset_does_not_have_is_rejected()
    {
        var map = Layer(new MapLayer("public.events", 0) { TimeFields = new TemporalExtentFields("begins", "ends") });

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            MapTimeFieldSchemas.ValidateAsync(map, (_, _, _) => Task.FromResult(Description(Name, Starts, Ends))));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("begins", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A designation over a field that is not a date is rejected by name: the
    /// readers read the designated bounds as instants, so accepting it would be
    /// accepting a parameter and ignoring it.
    /// </summary>
    [Fact]
    public async Task A_designation_over_a_field_that_is_not_a_date_is_rejected()
    {
        var map = Layer(new MapLayer("public.events", 0) { TimeFields = new TemporalExtentFields("name", "ends") });

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            MapTimeFieldSchemas.ValidateAsync(map, (_, _, _) => Task.FromResult(Description(Name, Starts, Ends))));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("name", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_map_that_designs_nothing_costs_no_catalogue_call()
    {
        var map = Layer(new MapLayer("public.events", 0));

        await MapTimeFieldSchemas.ValidateAsync(
            map, (_, _, _) => throw new InvalidOperationException("no description was asked for"));
    }

    // ---- declared configuration ----

    [Fact]
    public async Task A_declared_layer_reads_its_designation_from_configuration()
    {
        using var registry = Registry(new MapsOptions
        {
            Path = FilePath,
            Declared =
            [
                new DeclaredMapOptions
                {
                    Name = "events",
                    Store = "postgis",
                    Services = [nameof(MapServiceKind.MapServer)],
                    Layers =
                    [
                        new DeclaredLayerOptions
                        {
                            Dataset = "public.events",
                            LayerId = 0,
                            StartDateField = "starts",
                            EndDateField = "ends",
                        },
                    ],
                },
            ],
        });

        var map = await registry.GetAsync("events");

        Assert.Equal(new TemporalExtentFields("starts", "ends"), Assert.Single(map.Layers).TimeFields);
    }

    [Fact]
    public async Task A_declared_layer_that_names_no_dates_designs_nothing()
    {
        using var registry = Registry(new MapsOptions
        {
            Path = FilePath,
            Declared =
            [
                new DeclaredMapOptions
                {
                    Name = "plain",
                    Store = "postgis",
                    Services = [nameof(MapServiceKind.MapServer)],
                    Layers = [new DeclaredLayerOptions { Dataset = "public.events", LayerId = 0 }],
                },
            ],
        });

        var map = await registry.GetAsync("plain");

        Assert.Null(Assert.Single(map.Layers).TimeFields);
    }

    private static MapRegistry Registry(MapsOptions options) =>
        new(options, (_, _) => Task.FromResult<IReadOnlyList<DatasetSummary>>([]));
}