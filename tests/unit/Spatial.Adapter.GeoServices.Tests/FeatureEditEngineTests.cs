using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The Feature Service edit engine (spec §9.1.6–§9.1.9, ADR-0037/ADR-0038):
/// success and rollback paths over <see cref="FeatureEditEngine"/> directly,
/// including per-feature failure, transactional rollback, rollback failure
/// and cancellation. The fakes honour <see cref="CancellationToken"/> like a
/// real store, so a cancelled token must abort the edit even when a store
/// ignores it — long-running work is a cancellable <see cref="Task"/>
/// (AGENTS.md hard wall).
/// </summary>
public sealed class FeatureEditEngineTests
{
    private static readonly CoordinateReference Crs = CoordinateReference.Epsg(4326);

    [Fact]
    public async Task Add_assigns_an_object_id_for_each_feature()
    {
        var table = new FakeTable();
        var store = new FakeStore(table);
        var request = new EsriEditRequest(Adds("""{"attributes":{"name":"Vienna","population":1900000},"geometry":{"x":16.3738,"y":48.2082}}"""), [], [], null, false);

        var body = await ExecuteAsync(FeatureEditEngine.EditsAsync(EsriEditOperation.Add, table.Describe(), store, store, request, Crs, CancellationToken.None));

        var added = Assert.Single(body.GetProperty("addResults").EnumerateArray());
        Assert.True(added.GetProperty("success").GetBoolean());
        Assert.Equal(4, added.GetProperty("objectId").GetInt64());
        Assert.Equal(4, table.Count);
    }

    [Fact]
    public async Task A_malformed_add_fails_alone_without_rollback()
    {
        var table = new FakeTable();
        var store = new SimpleStore(table);
        var request = new EsriEditRequest(
            Adds(
                """{"attributes":{"name":"Valid","population":1},"geometry":{"x":0,"y":0}}""",
                """{"attributes":{"population":1},"geometry":{"x":1,"y":1}}"""),
            [], [], null, false);

        var body = await ExecuteAsync(FeatureEditEngine.EditsAsync(EsriEditOperation.Add, table.Describe(), store, store, request, Crs, CancellationToken.None));

        var results = body.GetProperty("addResults").EnumerateArray().ToArray();
        Assert.True(results[0].GetProperty("success").GetBoolean());
        Assert.False(results[1].GetProperty("success").GetBoolean());
        Assert.Equal(EsriErrorCodes.InvalidParameters, results[1].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(4, table.Count);
    }

    [Fact]
    public async Task Update_merges_partial_attributes_and_keeps_the_rest()
    {
        var table = new FakeTable();
        var store = new FakeStore(table);
        var request = new EsriEditRequest([], Updates("""{"attributes":{"OBJECTID":2,"population":9999999}}"""), [], null, false);

        var body = await ExecuteAsync(FeatureEditEngine.EditsAsync(EsriEditOperation.Update, table.Describe(), store, store, request, Crs, CancellationToken.None));

        var updated = Assert.Single(body.GetProperty("updateResults").EnumerateArray());
        Assert.True(updated.GetProperty("success").GetBoolean());
        Assert.Equal(2, updated.GetProperty("objectId").GetInt64());
        Assert.True(store.Lookups > 0);
        var paris = table.ById(2);
        Assert.Equal("Paris", paris["name"].StringValue);
        Assert.Equal(9999999, paris["population"].Int64Value);
    }

    [Fact]
    public async Task Update_falls_back_to_a_scan_when_the_store_has_no_lookup()
    {
        var table = new FakeTable();
        var store = new SimpleStore(table);
        var request = new EsriEditRequest([], Updates("""{"attributes":{"OBJECTID":2,"population":9999999}}"""), [], null, false);

        var body = await ExecuteAsync(FeatureEditEngine.EditsAsync(EsriEditOperation.Update, table.Describe(), store, store, request, Crs, CancellationToken.None));

        Assert.True(Assert.Single(body.GetProperty("updateResults").EnumerateArray()).GetProperty("success").GetBoolean());
        Assert.Equal(9999999, table.ById(2)["population"].Int64Value);
    }

    [Fact]
    public async Task An_unknown_object_id_fails_its_update_alone()
    {
        var table = new FakeTable();
        var store = new FakeStore(table);
        var request = new EsriEditRequest(
            [],
            Updates("""{"attributes":{"OBJECTID":2,"population":1}}""", """{"attributes":{"OBJECTID":77,"population":1}}"""),
            [], null, false);

        var body = await ExecuteAsync(FeatureEditEngine.EditsAsync(EsriEditOperation.Update, table.Describe(), store, store, request, Crs, CancellationToken.None));

        var results = body.GetProperty("updateResults").EnumerateArray().ToArray();
        Assert.True(results[0].GetProperty("success").GetBoolean());
        Assert.False(results[1].GetProperty("success").GetBoolean());
        Assert.Contains("77", results[1].GetProperty("error").GetProperty("description").GetString());
    }

    [Fact]
    public async Task An_update_without_an_object_id_fails_its_entry_alone()
    {
        var table = new FakeTable();
        var store = new FakeStore(table);
        var request = new EsriEditRequest([], Updates("""{"attributes":{"population":1}}"""), [], null, false);

        var body = await ExecuteAsync(FeatureEditEngine.EditsAsync(EsriEditOperation.Update, table.Describe(), store, store, request, Crs, CancellationToken.None));

        var updated = Assert.Single(body.GetProperty("updateResults").EnumerateArray());
        Assert.False(updated.GetProperty("success").GetBoolean());
        Assert.Equal(EsriErrorCodes.InvalidParameters, updated.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Update_applies_geometry_and_null_edits()
    {
        var table = new FakeTable();
        var store = new FakeStore(table);
        var request = new EsriEditRequest(
            [],
            Updates(
                """{"attributes":{"OBJECTID":1,"population":null},"geometry":{"x":5,"y":6}}""",
                """{"attributes":{"OBJECTID":3},"geometry":null}"""),
            [], null, false);

        var body = await ExecuteAsync(FeatureEditEngine.EditsAsync(EsriEditOperation.Update, table.Describe(), store, store, request, Crs, CancellationToken.None));

        var results = body.GetProperty("updateResults").EnumerateArray().ToArray();
        Assert.True(results[0].GetProperty("success").GetBoolean());
        Assert.False(table.ById(1)["geometry"].IsNull);
        Assert.True(table.ById(1)["population"].IsNull);
        // The layer geometry is non-nullable, so clearing it fails that feature alone.
        Assert.False(results[1].GetProperty("success").GetBoolean());
        Assert.False(table.ById(3)["geometry"].IsNull);
    }

    [Fact]
    public async Task Delete_by_ids_reports_each_object_id()
    {
        var table = new FakeTable();
        var store = new FakeStore(table);
        var request = new EsriEditRequest([], [], [3, 77], null, false);

        var body = await ExecuteAsync(FeatureEditEngine.EditsAsync(EsriEditOperation.Delete, table.Describe(), store, store, request, Crs, CancellationToken.None));

        var results = body.GetProperty("deleteResults").EnumerateArray().ToArray();
        Assert.True(results[0].GetProperty("success").GetBoolean());
        Assert.Equal(3, results[0].GetProperty("objectId").GetInt64());
        Assert.False(results[1].GetProperty("success").GetBoolean());
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public async Task Delete_by_where_matches_without_object_ids()
    {
        var table = new FakeTable();
        var store = new FakeStore(table);
        Assert.True(EsriFilterClause.TryParse("name = 'Berlin'", out var where, out var error), error);
        var request = new EsriEditRequest([], [], [], where, false);

        var body = await ExecuteAsync(FeatureEditEngine.EditsAsync(EsriEditOperation.Delete, table.Describe(), store, store, request, Crs, CancellationToken.None));

        var deleted = Assert.Single(body.GetProperty("deleteResults").EnumerateArray());
        Assert.True(deleted.GetProperty("success").GetBoolean());
        Assert.Equal(1, deleted.GetProperty("objectId").GetInt64());
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public async Task Delete_by_an_unmatched_where_returns_no_results()
    {
        var table = new FakeTable();
        var store = new FakeStore(table);
        Assert.True(EsriFilterClause.TryParse("name = 'Nowhere'", out var where, out var error), error);
        var request = new EsriEditRequest([], [], [], where, false);

        var body = await ExecuteAsync(FeatureEditEngine.EditsAsync(EsriEditOperation.Delete, table.Describe(), store, store, request, Crs, CancellationToken.None));

        Assert.Empty(body.GetProperty("deleteResults").EnumerateArray());
        Assert.Equal(3, table.Count);
    }

    [Fact]
    public async Task Apply_edits_returns_all_three_result_arrays()
    {
        var table = new FakeTable();
        var store = new FakeStore(table);
        var request = new EsriEditRequest(
            Adds("""{"attributes":{"name":"Oslo","population":700000},"geometry":{"x":10.7522,"y":59.9139}}"""),
            Updates("""{"attributes":{"OBJECTID":1,"population":3700000}}"""),
            [2], null, false);

        var body = await ExecuteAsync(FeatureEditEngine.EditsAsync(EsriEditOperation.Apply, table.Describe(), store, store, request, Crs, CancellationToken.None));

        Assert.True(Assert.Single(body.GetProperty("addResults").EnumerateArray()).GetProperty("success").GetBoolean());
        Assert.True(Assert.Single(body.GetProperty("updateResults").EnumerateArray()).GetProperty("success").GetBoolean());
        Assert.True(Assert.Single(body.GetProperty("deleteResults").EnumerateArray()).GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Apply_with_empty_updates_and_deletes_returns_empty_arrays()
    {
        var table = new FakeTable();
        var store = new FakeStore(table);
        var request = new EsriEditRequest(
            Adds("""{"attributes":{"name":"Oslo","population":1},"geometry":{"x":0,"y":0}}"""),
            [], [], null, false);

        var body = await ExecuteAsync(FeatureEditEngine.EditsAsync(EsriEditOperation.Apply, table.Describe(), store, store, request, Crs, CancellationToken.None));

        Assert.True(Assert.Single(body.GetProperty("addResults").EnumerateArray()).GetProperty("success").GetBoolean());
        Assert.Empty(body.GetProperty("updateResults").EnumerateArray());
        Assert.Empty(body.GetProperty("deleteResults").EnumerateArray());
    }

    [Fact]
    public async Task A_store_outcome_failure_fails_its_feature_alone()
    {
        var table = new FakeTable();
        var store = new SimpleStore(table) { FailAddFor = "Bad" };
        var request = new EsriEditRequest(
            Adds(
                """{"attributes":{"name":"Good","population":1},"geometry":{"x":0,"y":0}}""",
                """{"attributes":{"name":"Bad","population":1},"geometry":{"x":1,"y":1}}"""),
            [], [], null, true);

        // No transaction face, so even with rollbackOnFailure there is nothing to roll back:
        // the good feature stays while the failed one is reported per feature.
        var body = await ExecuteAsync(FeatureEditEngine.EditsAsync(EsriEditOperation.Add, table.Describe(), store, store, request, Crs, CancellationToken.None));

        var results = body.GetProperty("addResults").EnumerateArray().ToArray();
        Assert.True(results[0].GetProperty("success").GetBoolean());
        Assert.False(results[1].GetProperty("success").GetBoolean());
        Assert.Equal(4, table.Count);
    }

    [Fact]
    public async Task Rollback_on_failure_undoes_the_whole_batch()
    {
        var table = new FakeTable();
        var store = new FakeStore(table);
        var request = new EsriEditRequest(
            Adds(
                """{"attributes":{"name":"RollbackMe","population":1},"geometry":{"x":0,"y":0}}""",
                """{"attributes":{"population":1},"geometry":{"x":1,"y":1}}"""),
            [], [], null, true);

        var body = await ExecuteAsync(FeatureEditEngine.EditsAsync(EsriEditOperation.Add, table.Describe(), store, store, request, Crs, CancellationToken.None));

        Assert.All(body.GetProperty("addResults").EnumerateArray(), result => Assert.False(result.GetProperty("success").GetBoolean()));
        Assert.Equal(3, table.Count);
        Assert.Equal(1, store.Rollbacks);
    }

    [Fact]
    public async Task A_store_throw_rolls_back_and_propagates_the_original_failure()
    {
        var table = new FakeTable();
        var store = new FakeStore(table) { EditFault = SpatialException.Unavailable("the store is down") };
        var request = new EsriEditRequest(
            Adds("""{"attributes":{"name":"Lost","population":1},"geometry":{"x":0,"y":0}}"""),
            [], [], null, true);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            FeatureEditEngine.EditsAsync(EsriEditOperation.Add, table.Describe(), store, store, request, Crs, CancellationToken.None));

        Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
        Assert.Equal(1, store.Rollbacks);
        Assert.Equal(3, table.Count);
    }

    [Fact]
    public async Task A_rollback_failure_does_not_mask_the_original_failure()
    {
        var table = new FakeTable();
        var store = new FakeStore(table)
        {
            EditFault = SpatialException.Unavailable("the store is down"),
            FailRollback = true,
        };
        var request = new EsriEditRequest(
            Adds("""{"attributes":{"name":"Lost","population":1},"geometry":{"x":0,"y":0}}"""),
            [], [], null, true);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            FeatureEditEngine.EditsAsync(EsriEditOperation.Add, table.Describe(), store, store, request, Crs, CancellationToken.None));

        Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
    }

    [Fact]
    public async Task A_cancelled_token_aborts_before_any_store_work()
    {
        var table = new FakeTable();
        // The store ignores cancellation here on purpose: the engine itself must be cancellable.
        var store = new FakeStore(table) { IgnoreCancellation = true };
        var request = new EsriEditRequest(
            Adds("""{"attributes":{"name":"Never","population":1},"geometry":{"x":0,"y":0}}"""),
            [], [], null, true);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            FeatureEditEngine.EditsAsync(EsriEditOperation.Add, table.Describe(), store, store, request, Crs, new CancellationToken(canceled: true)));

        Assert.Equal(0, store.Begins);
        Assert.Equal(0, store.AddCalls);
        Assert.Equal(3, table.Count);
    }

    [Fact]
    public async Task Cancellation_mid_run_rolls_back_and_propagates()
    {
        var table = new FakeTable();
        using var cancelled = new CancellationTokenSource();
        var store = new FakeStore(table) { OnEdit = () => cancelled.Cancel() };
        var request = new EsriEditRequest(
            [],
            Updates("""{"attributes":{"OBJECTID":1,"population":5}}"""),
            [], null, true);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            FeatureEditEngine.EditsAsync(EsriEditOperation.Update, table.Describe(), store, store, request, Crs, cancelled.Token));

        Assert.Equal(1, store.Rollbacks);
    }

    [Fact]
    public async Task A_layer_without_an_integer_identity_cannot_be_edited()
    {
        var table = new FakeTable();
        var store = new FakeStore(table);
        var readOnly = table.Describe() with { IdColumns = [] };
        var request = new EsriEditRequest(
            Adds("""{"attributes":{"name":"Never","population":1},"geometry":{"x":0,"y":0}}"""),
            [], [], null, false);

        await Assert.ThrowsAsync<EsriInteropException>(() =>
            FeatureEditEngine.EditsAsync(EsriEditOperation.Add, readOnly, store, store, request, Crs, CancellationToken.None));
    }

    private static List<JsonElement> Adds(params string[] items) => Documents(items);

    private static List<JsonElement> Updates(params string[] items) => Documents(items);

    private static List<JsonElement> Documents(string[] items)
    {
        using var document = JsonDocument.Parse("[" + string.Join(",", items) + "]");
        return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToList();
    }

    private static async Task<JsonElement> ExecuteAsync(Task<IResult> pending)
    {
        var result = await pending;
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    /// <summary>The rows under test: an integer identity, a string, a nullable integer and a geometry.</summary>
    private sealed class FakeTable
    {
        public static readonly FeatureSchema Schema = new(
        [
            new FieldDefinition("id", AttributeKind.Int64, nullable: true),
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("population", AttributeKind.Int64, nullable: true),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]);

        private List<Feature> _features;
        private long _nextId = 4;

        public FakeTable()
        {
            _features =
            [
                Row(1, "Berlin", 3_664_000, 13.405, 52.52),
                Row(2, "Paris", 2_150_000, 2.3522, 48.8566),
                Row(3, "Rome", 2_870_000, 12.4964, 41.9028),
            ];
        }

        public int Count => _features.Count;

        public DatasetDescription Describe() =>
            new("memory.places", "memory", "places", "geometry", 4326, "Point", Count, ["id"], Schema);

        public Feature ById(long objectId) =>
            _features.Single(feature => feature["id"].Int64Value == objectId);

        public Feature[] Snapshot() => _features.ToArray();

        public void Restore(IReadOnlyList<Feature> snapshot) => _features = [.. snapshot];

        public Feature Insert(IReadOnlyList<AttributeValue> values)
        {
            var id = _nextId++;
            var row = values.ToArray();
            row[Schema.IndexOf("id")] = AttributeValue.FromInt64(id);
            var feature = new Feature(new FeatureId(id.ToString(CultureInfo.InvariantCulture)), Schema, row);
            _features.Add(feature);
            return feature;
        }

        public void Replace(Feature feature)
        {
            var index = _features.FindIndex(candidate => candidate.Id.Equals(feature.Id));
            if (index < 0)
            {
                throw new InvalidOperationException($"No feature has identity '{feature.Id}'.");
            }

            _features[index] = feature;
        }

        public bool Remove(FeatureId id) => _features.RemoveAll(candidate => candidate.Id.Equals(id)) > 0;

        private static Feature Row(long id, string name, long population, double x, double y) =>
            new(
                new FeatureId(id.ToString(CultureInfo.InvariantCulture)),
                Schema,
                [
                    AttributeValue.FromInt64(id),
                    AttributeValue.FromString(name),
                    AttributeValue.FromInt64(population),
                    AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(4326))),
                ]);
    }

    /// <summary>
    /// A full-capability fake: reads, editing, read-by-identity and
    /// transactions over one <see cref="FakeTable"/>. Every async boundary
    /// honours cancellation unless <see cref="IgnoreCancellation"/> is set,
    /// which proves the engine itself aborts a cancelled edit.
    /// </summary>
    private sealed class FakeStore : IFeatureStore, IFeatureEditStore, IFeatureLookup, ITransactionStore
    {
        private readonly FakeTable _table;
        private readonly Dictionary<string, IReadOnlyList<Feature>> _snapshots = new(StringComparer.Ordinal);

        public FakeStore(FakeTable table) => _table = table;

        public int Lookups { get; private set; }

        public int Begins { get; private set; }

        public int Commits { get; private set; }

        public int Rollbacks { get; private set; }

        public int AddCalls { get; private set; }

        public bool IgnoreCancellation { get; set; }

        public bool FailRollback { get; set; }

        public Exception? EditFault { get; set; }

        public Action? OnEdit { get; set; }

        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            Check(cancellationToken);
            IReadOnlyList<FeatureBatch> batches = [new FeatureBatch(FakeTable.Schema, _table.Snapshot())];
            return Task.FromResult(batches);
        }

        public Task<IReadOnlyList<FeatureBatch>> QueryAsync(string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default) =>
            ScanAsync(dataset, cancellationToken);

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default)
        {
            Check(cancellationToken);
            return Task.FromResult(0);
        }

        public Task<IReadOnlyList<Feature>> GetAsync(string dataset, IReadOnlyList<FeatureId> ids, CancellationToken cancellationToken = default)
        {
            Check(cancellationToken);
            Lookups++;
            var wanted = new HashSet<FeatureId>(ids);
            IReadOnlyList<Feature> found = _table.Snapshot().Where(feature => wanted.Contains(feature.Id)).ToArray();
            return Task.FromResult(found);
        }

        public Task<IReadOnlyList<FeatureEditOutcome>> AddAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default)
        {
            AddCalls++;
            OnEdit?.Invoke();
            Check(cancellationToken);
            if (EditFault is not null)
            {
                throw EditFault;
            }

            var outcomes = batch.Features
                .Select(feature => FeatureEditOutcome.Success(_table.Insert(feature.Attributes).Id))
                .ToArray();
            return Task.FromResult<IReadOnlyList<FeatureEditOutcome>>(outcomes);
        }

        public Task<IReadOnlyList<FeatureEditOutcome>> UpdateAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default)
        {
            OnEdit?.Invoke();
            Check(cancellationToken);
            if (EditFault is not null)
            {
                throw EditFault;
            }

            var outcomes = new List<FeatureEditOutcome>(batch.Count);
            foreach (var feature in batch.Features)
            {
                try
                {
                    _table.Replace(feature);
                    outcomes.Add(FeatureEditOutcome.Success(feature.Id));
                }
                catch (InvalidOperationException exception)
                {
                    outcomes.Add(FeatureEditOutcome.Failure(feature.Id, SpatialException.NotFound, exception.Message));
                }
            }

            return Task.FromResult<IReadOnlyList<FeatureEditOutcome>>(outcomes);
        }

        public Task<IReadOnlyList<FeatureEditOutcome>> DeleteAsync(string dataset, IReadOnlyList<FeatureId> featureIds, string? transaction = null, CancellationToken cancellationToken = default)
        {
            OnEdit?.Invoke();
            Check(cancellationToken);
            if (EditFault is not null)
            {
                throw EditFault;
            }

            var outcomes = featureIds
                .Select(id => _table.Remove(id)
                    ? FeatureEditOutcome.Success(id)
                    : FeatureEditOutcome.Failure(id, SpatialException.NotFound, "No such feature."))
                .ToArray();
            return Task.FromResult<IReadOnlyList<FeatureEditOutcome>>(outcomes);
        }

        public Task<string> BeginAsync(CancellationToken cancellationToken = default)
        {
            Check(cancellationToken);
            Begins++;
            var handle = Guid.NewGuid().ToString("N");
            _snapshots[handle] = _table.Snapshot();
            return Task.FromResult(handle);
        }

        public Task<bool> CommitAsync(string transaction, CancellationToken cancellationToken = default)
        {
            Check(cancellationToken);
            Commits++;
            _snapshots.Remove(transaction);
            return Task.FromResult(true);
        }

        public Task<bool> RollbackAsync(string transaction, CancellationToken cancellationToken = default)
        {
            Rollbacks++;
            if (FailRollback)
            {
                throw new InvalidOperationException("the rollback failed");
            }

            if (_snapshots.Remove(transaction, out var snapshot))
            {
                _table.Restore(snapshot);
            }

            return Task.FromResult(true);
        }

        private void Check(CancellationToken cancellationToken)
        {
            if (!IgnoreCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>
    /// A minimal fake without read-by-identity or transactions, so the engine
    /// must resolve targets by scan and report per-feature outcomes with
    /// nothing to roll back.
    /// </summary>
    private sealed class SimpleStore : IFeatureStore, IFeatureEditStore
    {
        private readonly FakeTable _table;

        public SimpleStore(FakeTable table) => _table = table;

        public string? FailAddFor { get; set; }

        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FeatureBatch> batches = [new FeatureBatch(FakeTable.Schema, _table.Snapshot())];
            return Task.FromResult(batches);
        }

        public Task<IReadOnlyList<FeatureBatch>> QueryAsync(string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default) =>
            ScanAsync(dataset, cancellationToken);

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }

        public Task<IReadOnlyList<FeatureEditOutcome>> AddAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcomes = new List<FeatureEditOutcome>(batch.Count);
            foreach (var feature in batch.Features)
            {
                var name = feature["name"].IsNull ? null : feature["name"].StringValue;
                if (name is not null && string.Equals(name, FailAddFor, StringComparison.Ordinal))
                {
                    outcomes.Add(FeatureEditOutcome.Failure(feature.Id, SpatialException.InvalidArguments, "The stub rejected the feature."));
                    continue;
                }

                outcomes.Add(FeatureEditOutcome.Success(_table.Insert(feature.Attributes).Id));
            }

            return Task.FromResult<IReadOnlyList<FeatureEditOutcome>>(outcomes);
        }

        public Task<IReadOnlyList<FeatureEditOutcome>> UpdateAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcomes = new List<FeatureEditOutcome>(batch.Count);
            foreach (var feature in batch.Features)
            {
                try
                {
                    _table.Replace(feature);
                    outcomes.Add(FeatureEditOutcome.Success(feature.Id));
                }
                catch (InvalidOperationException exception)
                {
                    outcomes.Add(FeatureEditOutcome.Failure(feature.Id, SpatialException.NotFound, exception.Message));
                }
            }

            return Task.FromResult<IReadOnlyList<FeatureEditOutcome>>(outcomes);
        }

        public Task<IReadOnlyList<FeatureEditOutcome>> DeleteAsync(string dataset, IReadOnlyList<FeatureId> featureIds, string? transaction = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcomes = featureIds
                .Select(id => _table.Remove(id)
                    ? FeatureEditOutcome.Success(id)
                    : FeatureEditOutcome.Failure(id, SpatialException.NotFound, "No such feature."))
                .ToArray();
            return Task.FromResult<IReadOnlyList<FeatureEditOutcome>>(outcomes);
        }
    }
}
