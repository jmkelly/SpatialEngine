using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.SqlServer;
using Spatial.Stores.SqlServer.Core;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// The description cache measured as a cache (ADR-0151, the SQL Server half of
/// ADR-0122): what it holds, what drops it, and what it costs when it is off.
/// Every case is free of SqlClient and of a container — the cache's whole input
/// is a dataset name, a description and a clock — and the store's own use of it
/// is proved against a real database in
/// <c>Spatial.SqlServer.Tests.SqlServerDescriptionCacheTests</c>.
/// </summary>
public sealed class SqlServerDescriptionCacheTests
{
    private static SqlServerDatasetName Dataset(string table)
    {
        Assert.True(SqlServerDatasetName.TryParse($"dbo.{table}", out var name, out var reason), reason);
        return name;
    }

    private static DatasetDescription Description(string id, long estimatedRows = 0) => new(
        id,
        "dbo",
        "places",
        "geometry",
        4326,
        "Point",
        estimatedRows,
        ["id"],
        new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]));

    private static MovableClock Clock() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>A description read once is served again without going anywhere.</summary>
    [Fact]
    public void A_held_description_is_served_until_it_is_dropped()
    {
        var cache = new SqlServerDescriptionCache(TimeSpan.FromMinutes(1), Clock());
        var name = Dataset("places");
        cache.Set(name, Description("dbo.places"));

        Assert.True(cache.TryGet(name, out var served));
        Assert.Equal("dbo.places", served.Id);
        Assert.Equal(1, cache.Count);
    }

    /// <summary>A dataset the store has not described is a miss, not a guess.</summary>
    [Fact]
    public void A_dataset_that_was_never_described_is_a_miss()
    {
        var cache = new SqlServerDescriptionCache(TimeSpan.FromMinutes(1), Clock());

        Assert.False(cache.TryGet(Dataset("other"), out _));
        Assert.Equal(0, cache.Count);
    }

    /// <summary>
    /// The expiry: a description older than the window is read again, so a
    /// schema changed outside the store is picked up rather than inherited.
    /// </summary>
    [Fact]
    public void A_description_older_than_the_window_is_read_again()
    {
        var clock = Clock();
        var cache = new SqlServerDescriptionCache(TimeSpan.FromMinutes(1), clock);
        var name = Dataset("places");
        cache.Set(name, Description("dbo.places"));

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True(cache.TryGet(name, out _));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(cache.TryGet(name, out _));
        Assert.Equal(0, cache.Count);
    }

    /// <summary>Invalidation drops one dataset's description and leaves the others.</summary>
    [Fact]
    public void Invalidating_a_dataset_drops_only_that_description()
    {
        var cache = new SqlServerDescriptionCache(TimeSpan.FromMinutes(1), Clock());
        cache.Set(Dataset("places"), Description("dbo.places"));
        cache.Set(Dataset("roads"), Description("dbo.roads"));

        cache.Invalidate(Dataset("places"));

        Assert.False(cache.TryGet(Dataset("places"), out _));
        Assert.True(cache.TryGet(Dataset("roads"), out _));
        Assert.Equal(1, cache.Count);
    }

    /// <summary>Invalidating an unknown dataset is a no-op, not a failure.</summary>
    [Fact]
    public void Invalidating_a_dataset_the_store_never_described_does_nothing()
    {
        var cache = new SqlServerDescriptionCache(TimeSpan.FromMinutes(1), Clock());
        cache.Set(Dataset("places"), Description("dbo.places"));

        cache.Invalidate(Dataset("never_seen"));
        cache.InvalidateAll();

        Assert.False(cache.TryGet(Dataset("places"), out _));
        Assert.Equal(0, cache.Count);
    }

    /// <summary>
    /// The cache switched off: the store's behaviour before ADR-0151, and the
    /// setting for a database whose schema moves on a schedule it cannot see.
    /// Nothing is held and every read is a miss.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_window_holds_nothing(int seconds)
    {
        var cache = new SqlServerDescriptionCache(TimeSpan.FromSeconds(seconds), Clock());
        var name = Dataset("places");
        cache.Set(name, Description("dbo.places"));

        Assert.False(cache.Enabled);
        Assert.False(cache.TryGet(name, out _));
        Assert.Equal(0, cache.Count);
    }

    /// <summary>
    /// The cost a walk is measured by: the store counts the descriptions that
    /// went to the catalogue, and a served description is not one of them.
    /// </summary>
    [Fact]
    public void A_served_description_is_not_a_read_of_the_catalogue()
    {
        var cache = new SqlServerDescriptionCache(TimeSpan.FromMinutes(1), Clock());
        var name = Dataset("places");

        cache.NoteRead();
        cache.Set(name, Description("dbo.places"));
        cache.TryGet(name, out _);
        cache.Invalidate(name);
        cache.TryGet(name, out _);

        Assert.Equal(1, cache.Reads);
    }

    /// <summary>A clock the test moves by hand, so expiry needs no sleeping.</summary>
    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
