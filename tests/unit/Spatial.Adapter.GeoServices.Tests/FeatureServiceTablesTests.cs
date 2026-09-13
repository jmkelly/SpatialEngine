using Spatial.Core.Features;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// T-028: the FeatureServer root exposes non-spatial datasets under
/// <c>tables</c> (what <c>getAllLayersAndTables</c> reads), with stable ids
/// from the single layer/table id space. A dataset is a table when its
/// schema carries no geometry field.
/// </summary>
public sealed class FeatureServiceTablesTests
{
    private static DatasetDescription Spatial(string id) => new(
        id, "demo", "places", "geometry", 4326, "Point", 3, ["id"],
        new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]));

    private static DatasetDescription Tabular(string id) => new(
        id, "demo", "counts", string.Empty, 0, "Unknown", 3, ["id"],
        new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64),
            new FieldDefinition("name", AttributeKind.String, nullable: true),
        ]));

    [Fact]
    public void A_dataset_without_a_geometry_field_is_a_table()
    {
        Assert.False(EsriLayerModel.IsTable(Spatial("demo.places")));
        Assert.True(EsriLayerModel.IsTable(Tabular("demo.counts")));
    }

    [Fact]
    public void The_root_lists_tables_beside_layers_with_stable_ids()
    {
        var layers = new[] { new PublishedLayer(0, "demo.places", "Places") };
        var tables = new[] { new PublishedLayer(1, "demo.counts", "Counts") };

        var root = FeatureService.Root(layers, tables, editable: false);

        var layer = Assert.Single(root.Layers);
        Assert.Equal((0, "Places"), (layer.Id, layer.Name));
        var table = Assert.Single(root.Tables);
        Assert.Equal((1, "Counts"), (table.Id, table.Name));
    }

    [Fact]
    public void Table_references_and_metadata_carry_the_table_type()
    {
        Assert.Equal("Table", EsriLayerModel.Reference(1, "Counts", isTable: true).Type);
        Assert.Equal("Feature Layer", EsriLayerModel.Reference(0, "Places", isTable: false).Type);

        Assert.Equal("Table", EsriLayerModel.Describe(1, Tabular("demo.counts"), editable: false, isTable: true).Type);
        Assert.Equal("Feature Layer", EsriLayerModel.Describe(0, Spatial("demo.places"), editable: false, isTable: false).Type);
    }
}
