using Microsoft.AspNetCore.Hosting;
using Spatial.Client;
using Spatial.Core.Features;

namespace Spatial.Host.Tests;

/// <summary>
/// The store mutation surface (ADR-0033, T-003): dataset creation and the
/// begin/commit/rollback transaction lifecycle over HTTP against the always
/// available writable in-memory provider, covering success, failure and
/// cancellation. Every dataset is <c>public.txn_&lt;guid&gt;</c>, so one host
/// serves the whole class (ADR-0160, ADR-0161) without the shared memory store
/// leaking a dataset between tests.
/// </summary>
public sealed class StoreTransactionTests : IClassFixture<StoreTransactionTests.StoreHost>
{
    private const string Store = "memory";

    private readonly StoreHost _host;

    public StoreTransactionTests(StoreHost host) => _host = host;

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static FeatureBatch SampleBatch() => new(Schema, []);

    private static string NewDataset() => $"public.txn_{Guid.NewGuid():N}";

    private static SpatialClient Client(HttpClient http) => new(http);

    [Fact]
    public async Task Create_returns_the_dataset_and_it_scans()
    {
        var client = Client(_host.Client);
        var dataset = NewDataset();

        var created = await client.CreateDatasetAsync(dataset, SampleBatch(), 4326, Store);

        Assert.Equal(dataset, created);
        var description = await client.DescribeDatasetAsync(dataset, Store);
        Assert.Equal(dataset, description.Id);
        var batches = await client.ScanAsync(dataset, Store);
        Assert.Empty(batches.SelectMany(batch => batch.Features));
    }

    [Fact]
    public async Task Create_duplicate_is_a_400()
    {
        var client = Client(_host.Client);
        var dataset = NewDataset();
        await client.CreateDatasetAsync(dataset, SampleBatch(), 4326, Store);

        var exception = await Assert.ThrowsAsync<SpatialClientException>(() =>
            client.CreateDatasetAsync(dataset, SampleBatch(), 4326, Store));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("invalid.arguments", exception.Code);
    }

    [Fact]
    public async Task Create_with_an_invalid_id_or_srid_is_a_400()
    {
        var client = Client(_host.Client);

        var badId = await Assert.ThrowsAsync<SpatialClientException>(() =>
            client.CreateDatasetAsync("no-table-separator", SampleBatch(), 4326, Store));
        Assert.Equal(400, badId.StatusCode);
        Assert.Equal("invalid.arguments", badId.Code);

        var badSrid = await Assert.ThrowsAsync<SpatialClientException>(() =>
            client.CreateDatasetAsync(NewDataset(), SampleBatch(), 0, Store));
        Assert.Equal(400, badSrid.StatusCode);
        Assert.Equal("invalid.arguments", badSrid.Code);
    }

    [Fact]
    public async Task Begin_and_commit_round_trip()
    {
        var client = Client(_host.Client);

        var transaction = await client.BeginTransactionAsync(Store);

        Assert.False(string.IsNullOrEmpty(transaction));
        Assert.True(await client.CommitTransactionAsync(transaction, Store));
    }

    [Fact]
    public async Task Begin_and_rollback_round_trip()
    {
        var client = Client(_host.Client);

        var transaction = await client.BeginTransactionAsync(Store);

        Assert.False(string.IsNullOrEmpty(transaction));
        Assert.True(await client.RollbackTransactionAsync(transaction, Store));
    }

    [Fact]
    public async Task Commit_of_an_unknown_transaction_reports_false()
    {
        var client = Client(_host.Client);

        Assert.False(await client.CommitTransactionAsync("missing", Store));
    }

    [Fact]
    public async Task Rollback_of_an_unknown_transaction_reports_false()
    {
        var client = Client(_host.Client);

        Assert.False(await client.RollbackTransactionAsync("missing", Store));
    }

    [Fact]
    public async Task Commit_and_rollback_without_a_transaction_store_are_a_400()
    {
        var client = Client(_host.Client);

        var commit = await Assert.ThrowsAsync<SpatialClientException>(() =>
            client.CommitTransactionAsync("whatever", "demo"));
        Assert.Equal(400, commit.StatusCode);
        Assert.Equal("invalid.arguments", commit.Code);

        var rollback = await Assert.ThrowsAsync<SpatialClientException>(() =>
            client.RollbackTransactionAsync("whatever", "demo"));
        Assert.Equal(400, rollback.StatusCode);
        Assert.Equal("invalid.arguments", rollback.Code);
    }

    [Fact]
    public async Task Cancelled_create_begin_commit_and_rollback_throw()
    {
        var client = Client(_host.Client);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.CreateDatasetAsync(NewDataset(), SampleBatch(), 4326, Store, cts.Token));
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.BeginTransactionAsync(Store, cts.Token));
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.CommitTransactionAsync("whatever", Store, cts.Token));
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.RollbackTransactionAsync("whatever", Store, cts.Token));
    }

    /// <summary>
    /// One host for the class, configured as the tests configure it today:
    /// no settings at all, because every dataset is the test's own.
    /// </summary>
    public sealed class StoreHost : ClassHostFixture
    {
        public StoreHost()
            : base("spatial-store-txn")
        {
        }

        protected override void ConfigureHost(IWebHostBuilder builder)
        {
            // The class's settings are the host's defaults.
        }
    }
}
