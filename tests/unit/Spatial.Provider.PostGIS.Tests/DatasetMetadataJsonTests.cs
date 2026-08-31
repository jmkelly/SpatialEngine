using Spatial.Core.Features;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.PostGIS.Tests;

/// <summary>
/// The shared dataset-metadata JSON interchange (ADR-0028): the
/// <c>dataset.description</c> document must survive a write/read round trip
/// with every field kind, and an unknown attribute-kind name is rejected with
/// a message naming the offending field.
/// </summary>
public sealed class DatasetMetadataJsonTests
{
    [Fact]
    public void Description_survives_a_write_read_round_trip()
    {
        var description = FeatureTests.PlacesDescription(FeatureTests.Schema(
            ("id", AttributeKind.Int64, false),
            ("name", AttributeKind.String, false),
            ("population", AttributeKind.Double, true),
            ("geom", AttributeKind.Geometry, false)));

        var json = DatasetMetadataJson.WriteDescription(description);
        var decoded = DatasetMetadataJson.ReadDescription(json);

        Assert.Equal(description.Id, decoded.Id);
        Assert.Equal(description.SchemaName, decoded.SchemaName);
        Assert.Equal(description.Table, decoded.Table);
        Assert.Equal(description.GeometryColumn, decoded.GeometryColumn);
        Assert.Equal(description.Srid, decoded.Srid);
        Assert.Equal(description.GeometryType, decoded.GeometryType);
        Assert.Equal(description.EstimatedRowCount, decoded.EstimatedRowCount);
        Assert.Equal(description.IdColumns, decoded.IdColumns);
        Assert.Equal(description.Schema.Fields.Select(field => $"{field.Name}|{field.Kind}|{field.Nullable}"),
            decoded.Schema.Fields.Select(field => $"{field.Name}|{field.Kind}|{field.Nullable}"));
    }

    [Fact]
    public void ReadDescription_rejects_an_unknown_attribute_kind_naming_the_field()
    {
        var json = DatasetMetadataJson.WriteDescription(FeatureTests.PlacesDescription());

        var malformed = json.Replace("\"kind\":\"Int64\"", "\"kind\":\"Banana\"");

        var exception = Assert.Throws<ArgumentException>(() => DatasetMetadataJson.ReadDescription(malformed));
        Assert.Contains("'id'", exception.Message);
        Assert.Contains("'Banana'", exception.Message);
    }
}
