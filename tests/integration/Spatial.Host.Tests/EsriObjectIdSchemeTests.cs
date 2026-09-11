using Spatial.Adapter.GeoServices;
using Spatial.Core.Features;
using Spatial.PluginSdk.Providers;

namespace Spatial.Host.Tests;

/// <summary>
/// The layer object-id scheme (ADR-0037): only a dataset whose single
/// identity column is an integer exposes an editable <c>OBJECTID</c>;
/// everything else keeps the read-only synthetic scan ordinal.
/// </summary>
public sealed class EsriObjectIdSchemeTests
{
    [Fact]
    public void An_integer_identity_column_enables_editing()
    {
        var dataset = Describe(new FieldDefinition("id", AttributeKind.Int64), ["id"]);

        var scheme = EsriObjectIdScheme.For(dataset);

        Assert.True(scheme.SupportsEditing);
        var feature = Feature(dataset.Schema, AttributeValue.FromInt64(7));
        Assert.True(scheme.TryResolve(feature, ordinal: 1, out var objectId));
        Assert.Equal(7, objectId);
    }

    [Fact]
    public void A_string_identity_column_keeps_the_synthetic_ordinal()
    {
        var dataset = Describe(new FieldDefinition("code", AttributeKind.String), ["code"]);

        var scheme = EsriObjectIdScheme.For(dataset);

        Assert.False(scheme.SupportsEditing);
        var feature = Feature(dataset.Schema, AttributeValue.FromString("berlin"));
        Assert.True(scheme.TryResolve(feature, ordinal: 3, out var objectId));
        Assert.Equal(3, objectId);
    }

    [Fact]
    public void A_composite_identity_keeps_the_synthetic_ordinal()
    {
        var dataset = Describe(new FieldDefinition("id", AttributeKind.Int64), ["id", "tenant"]);

        Assert.False(EsriObjectIdScheme.For(dataset).SupportsEditing);
    }

    private static DatasetDescription Describe(FieldDefinition identity, IReadOnlyList<string> identityColumns)
    {
        var schema = new FeatureSchema([identity, new FieldDefinition("geometry", AttributeKind.Geometry)]);
        return new DatasetDescription("test.places", "test", "places", "geometry", 4326, "Point", 0, identityColumns, schema);
    }

    private static Feature Feature(FeatureSchema schema, AttributeValue identity) =>
        new(new FeatureId("x"), schema, [identity, AttributeValue.FromGeometry(Spatial.Core.Geometry.GeometryFactory.CreatePoint(0, 0))]);
}
