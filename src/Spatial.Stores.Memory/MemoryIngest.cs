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
/// store, differing only in durability.
/// </summary>
public sealed class MemoryIngest : IDatasetIngest
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
        return Task.FromResult(_store.WithLock(() =>
        {
            if (_store.Catalog.Contains(plan.Dataset))
            {
                throw SpatialException.BadArguments($"Dataset '{plan.Dataset}' already exists.");
            }

            var dataset = plan.Materialise();
            _store.Catalog.Add(dataset);
            return new IngestOutcome(dataset.Id, dataset.Features.Count, dataset.Srid, plan.IdentityColumn);
        }));
    }
}
