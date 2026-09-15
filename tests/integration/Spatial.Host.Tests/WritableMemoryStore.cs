using System.Globalization;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Host.Tests;

/// <summary>
/// A writable in-memory store double for the GeoServices editing tests
/// (ADR-0037, ADR-0038). It models a real spatial table: one dataset with a
/// single integer identity column (<c>id</c>), a string, a nullable integer
/// and a geometry, served identically to <c>PostgisStore</c> so the facade's
/// object-id and edit paths are exercised over HTTP without Docker. It
/// implements the store contracts (catalogue, feature read/write, editing,
/// transactions and read-by-identity) so the facade advertises and accepts
/// edits and resolves them through the lookup rather than a scan.
/// </summary>
public sealed class WritableMemoryStore : IDataCatalogue, IFeatureStore, IFeatureLookup, ITransactionStore
{
    private const string DatasetId = "memory.places";
    private const string IdColumn = "id";

    private readonly object _gate = new();
    private readonly Dictionary<string, List<Feature>> _snapshots = new(StringComparer.Ordinal);
    private List<Feature> _features;
    private long _nextId;

    /// <summary>How many read-by-identity calls the store has served (used to prove the facade does not scan, ADR-0038).</summary>
    public int Lookups { get; private set; }

    public WritableMemoryStore()
    {
        var geometry = GeometryFactory.CreatePoint(13.405, 52.52, CoordinateReference.Epsg(4326));
        _features =
        [
            Feature(1, "Berlin", 3_664_000, geometry),
            Feature(2, "Paris", 2_150_000, GeometryFactory.CreatePoint(2.3522, 48.8566, CoordinateReference.Epsg(4326))),
            Feature(3, "Rome", 2_870_000, GeometryFactory.CreatePoint(12.4964, 41.9028, CoordinateReference.Epsg(4326))),
        ];
        _nextId = 4;
    }

    /// <summary>The dataset's schema: <c>id</c> (identity, nullable so adds may omit it), <c>name</c>, <c>population</c>, <c>geometry</c>.</summary>
    public static FeatureSchema Schema { get; } = new(
    [
        new FieldDefinition(IdColumn, AttributeKind.Int64, nullable: true),
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    public Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DatasetSummary> summaries = [new DatasetSummary(DatasetId, "memory", "places", "geometry", 4326, Count())];
        return Task.FromResult(summaries);
    }

    public Task<DatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default) =>
        Task.FromResult(Describe());

    public Task<string> CreateAsync(string dataset, FeatureBatch sample, int srid, CancellationToken cancellationToken = default) =>
        throw SpatialException.BadArguments("The in-memory store does not create datasets.");

    public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<FeatureBatch> batches = [new FeatureBatch(Schema, _features.ToArray())];
            return Task.FromResult(batches);
        }
    }

    public Task<IReadOnlyList<FeatureBatch>> QueryAsync(string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var features = bbox is null
                ? _features
                : _features.Where(feature => Intersects(feature, bbox)).ToList();
            IReadOnlyList<FeatureBatch> batches = [new FeatureBatch(Schema, features.ToArray())];
            return Task.FromResult(batches);
        }
    }

    public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (_gate)
        {
            _features.AddRange(batch.Features);
            return Task.FromResult(batch.Count);
        }
    }

    public Task<IReadOnlyList<Feature>> GetAsync(string dataset, IReadOnlyList<FeatureId> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Lookups++;
        lock (_gate)
        {
            var wanted = new HashSet<FeatureId>(ids);
            IReadOnlyList<Feature> found = _features.Where(feature => wanted.Contains(feature.Id)).ToArray();
            return Task.FromResult(found);
        }
    }

    public Task<IReadOnlyList<FeatureEditOutcome>> AddAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (_gate)
        {
            var outcomes = new List<FeatureEditOutcome>(batch.Count);
            foreach (var feature in batch.Features)
            {
                var id = _nextId++;
                var values = feature.Attributes.ToArray();
                values[Schema.IndexOf(IdColumn)] = AttributeValue.FromInt64(id);
                var stored = new Feature(new FeatureId(id.ToString(CultureInfo.InvariantCulture)), Schema, values);
                _features.Add(stored);
                outcomes.Add(FeatureEditOutcome.Success(stored.Id));
            }

            return Task.FromResult<IReadOnlyList<FeatureEditOutcome>>(outcomes);
        }
    }

    public Task<IReadOnlyList<FeatureEditOutcome>> UpdateAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (_gate)
        {
            var outcomes = new List<FeatureEditOutcome>(batch.Count);
            foreach (var feature in batch.Features)
            {
                var index = _features.FindIndex(candidate => candidate.Id.Equals(feature.Id));
                if (index < 0)
                {
                    outcomes.Add(FeatureEditOutcome.Failure(feature.Id, SpatialException.NotFound, "No such feature."));
                    continue;
                }

                _features[index] = feature;
                outcomes.Add(FeatureEditOutcome.Success(feature.Id));
            }

            return Task.FromResult<IReadOnlyList<FeatureEditOutcome>>(outcomes);
        }
    }

    public Task<IReadOnlyList<FeatureEditOutcome>> DeleteAsync(string dataset, IReadOnlyList<FeatureId> featureIds, string? transaction = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(featureIds);
        lock (_gate)
        {
            var outcomes = new List<FeatureEditOutcome>(featureIds.Count);
            foreach (var id in featureIds)
            {
                var removed = _features.RemoveAll(candidate => candidate.Id.Equals(id));
                outcomes.Add(removed > 0
                    ? FeatureEditOutcome.Success(id)
                    : FeatureEditOutcome.Failure(id, SpatialException.NotFound, "No such feature."));
            }

            return Task.FromResult<IReadOnlyList<FeatureEditOutcome>>(outcomes);
        }
    }

    public Task<string> BeginAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var handle = Guid.NewGuid().ToString("N");
            _snapshots[handle] = _features.ToList();
            return Task.FromResult(handle);
        }
    }

    public Task<bool> CommitAsync(string transaction, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _snapshots.Remove(transaction);
            return Task.FromResult(true);
        }
    }

    public Task<bool> RollbackAsync(string transaction, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_snapshots.Remove(transaction, out var snapshot))
            {
                _features = snapshot;
            }

            return Task.FromResult(true);
        }
    }

    private static DatasetDescription Describe() =>
        new(DatasetId, "memory", "places", "geometry", 4326, "Point", 3, [IdColumn], Schema);

    private static Feature Feature(long id, string name, long population, IGeometry geometry) =>
        new(
            new FeatureId(id.ToString(CultureInfo.InvariantCulture)),
            Schema,
            [
                AttributeValue.FromInt64(id),
                AttributeValue.FromString(name),
                AttributeValue.FromInt64(population),
                AttributeValue.FromGeometry(geometry),
            ]);

    private int Count()
    {
        lock (_gate)
        {
            return _features.Count;
        }
    }

    private static bool Intersects(Feature feature, BoundingBox bbox)
    {
        var index = feature.Schema.IndexOf("geometry");
        if (index < 0 || feature[index].Kind != AttributeKind.Geometry || feature[index].GeometryValue.Envelope is not { } envelope)
        {
            return false;
        }

        return envelope.MinX <= bbox.MaxX && envelope.MaxX >= bbox.MinX
            && envelope.MinY <= bbox.MaxY && envelope.MaxY >= bbox.MinY;
    }
}

/// <summary>
/// The editing face over <see cref="WritableMemoryStore"/>, split out
/// exactly like the production <c>PostgisEditStore</c> (ADR-0037) so the
/// facade must resolve the separately-registered face rather than the
/// read-store instance.
/// </summary>
public sealed class WritableMemoryEditor : IFeatureEditStore
{
    private readonly WritableMemoryStore _store;

    public WritableMemoryEditor(WritableMemoryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public Task<IReadOnlyList<FeatureEditOutcome>> AddAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
        _store.AddAsync(dataset, batch, transaction, cancellationToken);

    public Task<IReadOnlyList<FeatureEditOutcome>> UpdateAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
        _store.UpdateAsync(dataset, batch, transaction, cancellationToken);

    public Task<IReadOnlyList<FeatureEditOutcome>> DeleteAsync(string dataset, IReadOnlyList<FeatureId> featureIds, string? transaction = null, CancellationToken cancellationToken = default) =>
        _store.DeleteAsync(dataset, featureIds, transaction, cancellationToken);
}

/// <summary>
/// Wraps a writable memory store as a plain <see cref="IFeatureStore"/> and
/// <see cref="ITransactionStore"/> <em>without</em> declaring
/// <see cref="IFeatureLookup"/>, so the GeoServices facade is forced onto its
/// scan fallback for update/delete (ADR-0038).
/// </summary>
public sealed class ScanOnlyMemoryStore : IFeatureStore, ITransactionStore
{
    private readonly WritableMemoryStore _store;

    public ScanOnlyMemoryStore(WritableMemoryStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default) =>
        _store.ScanAsync(dataset, cancellationToken);

    public Task<IReadOnlyList<FeatureBatch>> QueryAsync(string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default) =>
        _store.QueryAsync(dataset, bbox, filter, cancellationToken);

    public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
        _store.WriteAsync(dataset, batch, transaction, cancellationToken);

    public Task<string> BeginAsync(CancellationToken cancellationToken = default) =>
        _store.BeginAsync(cancellationToken);

    public Task<bool> CommitAsync(string transaction, CancellationToken cancellationToken = default) =>
        _store.CommitAsync(transaction, cancellationToken);

    public Task<bool> RollbackAsync(string transaction, CancellationToken cancellationToken = default) =>
        _store.RollbackAsync(transaction, cancellationToken);
}
