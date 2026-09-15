using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.Memory.Tests;

/// <summary>
/// The in-memory ingest identity rules (ADR-0042): <c>Source</c> mode needs a
/// named Int64 field, so a missing, unknown or mistyped identity field is an
/// <c>invalid.arguments</c> failure before anything is stored.
/// </summary>
public sealed class MemoryIngestPlanTests
{
    private static readonly FeatureSchema Source = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static FeatureBatch Batch(params Feature[] features) => new(Source, features);

    private static Feature City(string id, string name) =>
        new(
            new FeatureId(id),
            Source,
            [
                AttributeValue.FromString(name),
                AttributeValue.FromInt64(1),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326))),
            ]);

    [Fact]
    public void Source_without_an_identity_field_is_rejected()
    {
        var failure = Assert.Throws<SpatialException>(() => MemoryIngestPlan.Create(
            new IngestRequest("memory.cities", 4326, IngestIdentity.Source),
            [Batch(City("1", "Berlin"))]));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("requires an IdentityField name", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_with_an_unknown_identity_field_is_rejected()
    {
        var failure = Assert.Throws<SpatialException>(() => MemoryIngestPlan.Create(
            new IngestRequest("memory.cities", 4326, IngestIdentity.Source, "nope"),
            [Batch(City("1", "Berlin"))]));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("is not a field of the ingest schema", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_with_a_non_int64_identity_field_is_rejected()
    {
        var failure = Assert.Throws<SpatialException>(() => MemoryIngestPlan.Create(
            new IngestRequest("memory.cities", 4326, IngestIdentity.Source, "name"),
            [Batch(City("1", "Berlin"))]));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("must be Int64", failure.Message, StringComparison.Ordinal);
    }
}
