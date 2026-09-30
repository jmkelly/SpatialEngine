using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Stores.Memory.Tests;

/// <summary>
/// A source-identity ingest keys its stored features by the identity column's
/// value, not by the position the decode read them at (ADR-0038,
/// ADR-0112 point 2). The codec numbers features as it reads them, so a
/// GeoJSON whose <c>id</c> runs 10..13 arrives with <c>Feature.Id</c> 1..4;
/// a dataset whose identity column is the same <c>id</c> must report the
/// column's value as the feature identity, or a read-by-identity asked for
/// object 13 misses a feature that exists — and a keyed read that buys
/// nothing is the scan it was meant to replace.
/// </summary>
public sealed class MemorySourceIdentityTests
{
    private static readonly FeatureSchema Source = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static Feature City(string decodedId, long identity, string name) =>
        new(
            new FeatureId(decodedId),
            Source,
            [
                AttributeValue.FromInt64(identity),
                AttributeValue.FromString(name),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326))),
            ]);

    private static FeatureBatch Batch(params Feature[] features) => new(Source, features);

    /// <summary>
    /// The stored features carry the identity column's value as their
    /// <see cref="Feature.Id"/>, so the number the decode read them at does
    /// not survive into the dataset.
    /// </summary>
    [Fact]
    public async Task A_source_identity_ingest_stores_the_identity_column_as_the_feature_identity()
    {
        var store = new MemoryStore();
        var ingest = new MemoryIngest(store);

        await ingest.IngestAsync(
            new IngestRequest("memory.children", 4326, IngestIdentity.Source, "id"),
            [Batch(City("1", 10, "Mitte"), City("2", 11, "Kreuzberg"), City("3", 12, "Belleville"), City("4", 13, "Orphan"))]);

        var stored = store.Catalog.Find("memory.children").Features;
        Assert.Equal(["10", "11", "12", "13"], stored.Select(feature => feature.Id.Value));
    }

    /// <summary>
    /// The read-by-identity face therefore resolves the object id: a lookup
    /// for 13 returns the feature whose identity column is 13, which is the
    /// whole point of the face.
    /// </summary>
    [Fact]
    public async Task A_source_identity_dataset_resolves_a_read_by_identity()
    {
        var store = new MemoryStore();
        var ingest = new MemoryIngest(store);

        await ingest.IngestAsync(
            new IngestRequest("memory.children", 4326, IngestIdentity.Source, "id"),
            [Batch(City("1", 10, "Mitte"), City("2", 11, "Kreuzberg"), City("3", 12, "Belleville"), City("4", 13, "Orphan"))]);

        var found = await store.GetAsync("memory.children", [new FeatureId("13"), new FeatureId("10")]);

        Assert.Equal(
            [("10", "Mitte"), ("13", "Orphan")],
            found.Select(feature => (feature.Id.Value, feature[Source.IndexOf("name")].StringValue)).OrderBy(entry => entry.Item1).ToArray());
    }

    /// <summary>
    /// An auto-identity ingest is unchanged: the assigned id is both the
    /// identity column's value and the feature identity, as before.
    /// </summary>
    [Fact]
    public async Task An_auto_identity_ingest_still_numbers_features_from_one()
    {
        var store = new MemoryStore();
        var ingest = new MemoryIngest(store);
        var withoutId = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]);

        await ingest.IngestAsync(
            new IngestRequest("memory.cities", 4326, IngestIdentity.Auto),
            [new FeatureBatch(withoutId,
            [
                new Feature(new FeatureId("10"), withoutId,
                [
                    AttributeValue.FromString("Mitte"),
                    AttributeValue.FromGeometry(GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326))),
                ]),
                new Feature(new FeatureId("11"), withoutId,
                [
                    AttributeValue.FromString("Kreuzberg"),
                    AttributeValue.FromGeometry(GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326))),
                ]),
            ])]);

        var stored = store.Catalog.Find("memory.cities").Features;
        Assert.Equal(["1", "2"], stored.Select(feature => feature.Id.Value));
        Assert.Equal([1L, 2L], stored.Select(feature => feature[stored[0].Schema.IndexOf("id")].Int64Value));
    }

    /// <summary>
    /// A source identity that is not a value cannot key a feature, so an
    /// ingest carrying one is a typed <c>invalid.arguments</c> failure and
    /// nothing is stored — rather than a row keyed by the decode's number,
    /// which is what a lookup for the object's own id would then miss.
    /// </summary>
    [Fact]
    public async Task A_source_identity_ingest_with_a_null_identity_stores_nothing()
    {
        var store = new MemoryStore();
        var ingest = new MemoryIngest(store);
        var nullable = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, nullable: true),
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
        ]);
        var page = new FeatureBatch(nullable,
        [
            new Feature(new FeatureId("1"), nullable,
            [
                AttributeValue.Null,
                AttributeValue.FromString("Mitte"),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326))),
            ]),
        ]);

        var failure = await Assert.ThrowsAsync<SpatialException>(
            () => ingest.IngestAsync(new IngestRequest("memory.children", 4326, IngestIdentity.Source, "id"), [page]));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.False(store.Catalog.Contains("memory.children"));
    }

    /// <summary>
    /// An auto-identity ingest keys its stored features by the identity column
    /// the store assigned, not by the identity the decode read them at
    /// (ADR-0119) — the same rule the source-identity path above follows, now
    /// that every ingest has an identity column (ADR-0149).
    /// </summary>
    [Fact]
    public async Task An_auto_identity_ingest_keys_its_features_by_the_assigned_column()
    {
        var store = new MemoryStore();
        var ingest = new MemoryIngest(store);
        var withoutId = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]);

        await ingest.IngestAsync(
            new IngestRequest("memory.cities", 4326),
            [new FeatureBatch(withoutId,
            [
                new Feature(new FeatureId("7"), withoutId,
                [
                    AttributeValue.FromString("Mitte"),
                    AttributeValue.FromGeometry(GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326))),
                ]),
            ])]);

        var stored = Assert.Single(store.Catalog.Find("memory.cities").Features);
        Assert.Equal("1", stored.Id.Value);
        Assert.Equal(["id"], (await store.DescribeAsync("memory.cities")).IdColumns);
    }
}
