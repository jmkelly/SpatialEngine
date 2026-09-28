using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;

namespace Spatial.Stores.Memory;

/// <summary>
/// The in-memory ingest face (ADR-0041 §3, ADR-0042): validate a decoded
/// upload into a <see cref="MemoryIngestPlan"/>, then build and register the
/// dataset in one in-memory operation, so a partial dataset is never
/// observable. Identity follows the same
/// <see cref="IngestIdentity.None|Auto|Source"/> rules as the PostGIS ingest
/// store, differing only in durability. It also implements
/// <see cref="IDatasetIngestStream"/>, which the dataset must keep anyway; what
/// streaming saves is the decoded document, not the stored features.
/// </summary>
public sealed class MemoryIngest : IDatasetIngest, IDatasetIngestStream
{
    private readonly MemoryStore _store;

    public MemoryIngest(MemoryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public Task<IngestOutcome> IngestAsync(
        IngestRequest request, IReadOnlyList<FeatureBatch> pages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pages);
        cancellationToken.ThrowIfCancellationRequested();
        var plan = MemoryIngestPlan.Create(request, pages);
        return Task.FromResult(Load(plan));
    }

    /// <inheritdoc />
    public async Task<IngestOutcome> IngestStreamAsync(
        IngestRequest request,
        FeatureSchema schema,
        IAsyncEnumerable<FeatureBatch> pages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(pages);
        cancellationToken.ThrowIfCancellationRequested();

        var plan = MemoryIngestPlan.Create(request, schema);
        var collected = new List<FeatureBatch>();
        await foreach (var page in pages.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            collected.Add(page);
        }

        return Load(plan.Bind(collected));
    }

    /// <summary>
    /// Registers the dataset in one locked operation, so a partial dataset is
    /// never observable: the catalogue only ever sees a fully built one.
    /// </summary>
    private IngestOutcome Load(MemoryIngestPlan plan) => _store.WithLock(() =>
    {
        if (_store.Catalog.Contains(plan.Dataset))
        {
            throw SpatialException.BadArguments($"Dataset '{plan.Dataset}' already exists.");
        }

        var dataset = plan.Materialise();
        _store.Catalog.Add(dataset);
        return new IngestOutcome(dataset.Id, dataset.Features.Count, dataset.Srid, plan.IdentityColumn);
    });
}
