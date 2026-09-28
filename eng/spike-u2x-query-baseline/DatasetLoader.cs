using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.Demo;
using Spatial.Stores.Memory;
using Spatial.Stores.PostGIS;

namespace Spatial.Spike.QueryBaseline;

/// <summary>
/// Loads the layer under test. The rows are the committed GeoNames
/// world-cities snapshot the demo store already serves (34,135 rows, CC-BY
/// 4.0 — <c>src/Spatial.Stores.Demo/Data/world-cities-attribution.txt</c>),
/// so the spike needs no network and is reproducible. They are copied into
/// the store under test, because the demo store is read-only and offers
/// neither an attribute filter nor the read-by-identity face.
/// <paramref name="reuse"/> keeps a table that is already loaded, so the
/// script can measure the same table before and after indexing.
/// </summary>
internal static class DatasetLoader
{
    private const string DemoDataset = "demo.world_cities";
    private const int Srid = 4326;
    private const int WriteChunk = 1000;

    /// <summary>The snapshot rows, read once from the demo store.</summary>
    internal static async Task<IReadOnlyList<Feature>> WorldCitiesAsync(CancellationToken cancellationToken)
    {
        var demo = new DemoStore();
        var batches = await demo.ScanAsync(DemoDataset, cancellationToken);
        return [.. batches.SelectMany(batch => batch.Features)];
    }

    /// <summary>
    /// Prepares the store under test with the snapshot: an empty defining
    /// batch, then chunked appends (skipped when <paramref name="reuse"/> is
    /// set and the table is already there). Returns the store plus its
    /// description and a resident snapshot of the rows (for the emulated
    /// ceiling and for the identities the per-feature read starts from).
    /// </summary>
    internal static async Task<LoadedStore> PrepareAsync(
        string storeKind,
        string? connectionString,
        bool reuse,
        CancellationToken cancellationToken)
    {
        var (catalogue, store, lookup) = Create(storeKind, connectionString);
        if (!reuse)
        {
            var rows = await WorldCitiesAsync(cancellationToken);
            await catalogue.CreateAsync(QueryRunner.Dataset, new FeatureBatch(rows[0].Schema, []), Srid, cancellationToken);
            foreach (var chunk in rows.Chunk(WriteChunk))
            {
                await store.WriteAsync(QueryRunner.Dataset, new FeatureBatch(rows[0].Schema, chunk), cancellationToken: cancellationToken);
            }
        }

        var description = await catalogue.DescribeAsync(QueryRunner.Dataset, cancellationToken);
        var stored = await ReadAllAsync(store, cancellationToken);
        return new LoadedStore(store, lookup, description, stored);
    }

    private static (IDataCatalogue Catalogue, IFeatureStore Store, IFeatureLookup? Lookup) Create(
        string storeKind,
        string? connectionString) => storeKind switch
        {
            "memory" => AsTuple(new MemoryStore()),
            "postgis" => AsTuple(new PostgisStore(new PostgisOptions { ConnectionString = connectionString ?? string.Empty })),
            _ => throw new ArgumentException($"unknown store '{storeKind}': expected 'memory' or 'postgis'.", nameof(storeKind)),
        };

    private static (IDataCatalogue, IFeatureStore, IFeatureLookup?) AsTuple<T>(T store)
        where T : IDataCatalogue, IFeatureStore =>
        (store, store, store as IFeatureLookup);

    private static async Task<IReadOnlyList<Feature>> ReadAllAsync(IFeatureStore store, CancellationToken cancellationToken)
    {
        var batches = await store.ScanAsync(QueryRunner.Dataset, cancellationToken);
        return [.. batches.SelectMany(batch => batch.Features)];
    }
}

/// <summary>The store under test, the layer facts derived from it, and its resident rows.</summary>
internal sealed record LoadedStore(
    IFeatureStore Store,
    IFeatureLookup? Lookup,
    DatasetDescription Description,
    IReadOnlyList<Feature> Resident);
