using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Stores.Memory.Tests;

/// <summary>
/// The streaming ingest face (ADR-0041 §4). The claim it makes is that pages
/// can arrive as they are decoded and still land atomically, so the tests that
/// matter are the failure ones: a page that throws, a cancellation part-way
/// through, a page that does not share the declared schema — in each case the
/// dataset must not exist afterwards.
/// </summary>
public sealed class MemoryIngestStreamTests
{
    private static readonly FeatureSchema Source = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static Feature City(string id, string name) =>
        new(
            new FeatureId(id),
            Source,
            [
                AttributeValue.FromString(name),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(13.4, 52.5, CoordinateReference.Epsg(4326))),
            ]);

    private static FeatureBatch Batch(params Feature[] features) => new(Source, features);

    private static async IAsyncEnumerable<FeatureBatch> Pages(params FeatureBatch[] pages)
    {
        foreach (var page in pages)
        {
            await Task.Yield();
            yield return page;
        }
    }

    /// <summary>Pages, and then a page that throws part-way through the stream.</summary>
    private static async IAsyncEnumerable<FeatureBatch> Failing(FeatureBatch good, Exception failure)
    {
        yield return good;
        await Task.Yield();
        throw failure;
    }

    /// <summary>The dataset is not in the catalogue, under either spelling of its id.</summary>
    private static void AssertNoDataset(MemoryStore store, string dataset) =>
        Assert.False(
            store.Catalog.Contains(dataset) || store.Catalog.Contains(dataset.Replace("memory.", string.Empty, StringComparison.Ordinal)),
            $"'{dataset}' was registered; a failed or cancelled ingest must leave nothing behind.");

    private static (MemoryStore Store, MemoryIngest Ingest) NewStore()
    {
        var store = new MemoryStore();
        return (store, new MemoryIngest(store));
    }

    [Fact]
    public async Task Streamed_pages_load_and_report_the_feature_count()
    {
        var (store, ingest) = NewStore();

        var outcome = await ingest.IngestStreamAsync(
            new IngestRequest("memory.cities", 4326),
            Source,
            Pages(Batch(City("1", "Berlin")), Batch(City("2", "Paris"))));

        Assert.Equal(2, outcome.Features);
        Assert.Equal("memory.cities", outcome.Dataset);
        Assert.Equal(2, store.Catalog.Find("memory.cities").Features.Count);
    }

    [Fact]
    public async Task An_empty_stream_is_rejected_and_stores_nothing()
    {
        var (store, ingest) = NewStore();

        var failure = await Assert.ThrowsAsync<SpatialException>(() => ingest.IngestStreamAsync(
            new IngestRequest("memory.empty", 4326), Source, Pages()));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        AssertNoDataset(store, "memory.empty");
    }

    [Fact]
    public async Task A_page_that_does_not_share_the_declared_schema_is_rejected()
    {
        var (store, ingest) = NewStore();
        var other = new FeatureSchema([new FieldDefinition("n", AttributeKind.Int64)]);
        var stray = new FeatureBatch(other, [new Feature(new FeatureId("1"), other, [AttributeValue.FromInt64(1)])]);

        var failure = await Assert.ThrowsAsync<SpatialException>(() => ingest.IngestStreamAsync(
            new IngestRequest("memory.stray", 4326), Source, Pages(Batch(City("1", "Berlin")), stray)));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        AssertNoDataset(store, "memory.stray");
    }

    [Fact]
    public async Task A_failure_part_way_through_the_stream_stores_nothing()
    {
        var (store, ingest) = NewStore();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ingest.IngestStreamAsync(
            new IngestRequest("memory.broken", 4326),
            Source,
            Failing(Batch(City("1", "Berlin")), new InvalidOperationException("page 2 failed"))));

        AssertNoDataset(store, "memory.broken");
    }

    [Fact]
    public async Task Cancelling_mid_stream_stores_nothing()
    {
        var (store, ingest) = NewStore();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await ingest.IngestStreamAsync(
                new IngestRequest("memory.cancelled", 4326),
                Source,
                Cancelling(cancellation, Batch(City("1", "Berlin"))),
                cancellation.Token);
        });

        AssertNoDataset(store, "memory.cancelled");
    }

    /// <summary>One good page, then a cancellation before the next.</summary>
    private static async IAsyncEnumerable<FeatureBatch> Cancelling(
        CancellationTokenSource cancellation, FeatureBatch first)
    {
        yield return first;
        await cancellation.CancelAsync();
        await Task.Yield();
        cancellation.Token.ThrowIfCancellationRequested();
        yield return Batch(City("2", "Paris"));
    }

    [Fact]
    public async Task An_already_cancelled_token_loads_nothing_before_the_first_page()
    {
        var (store, ingest) = NewStore();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ingest.IngestStreamAsync(
            new IngestRequest("memory.cancelled", 4326), Source, Pages(Batch(City("1", "Berlin"))), cancellation.Token));

        AssertNoDataset(store, "memory.cancelled");
    }
}
