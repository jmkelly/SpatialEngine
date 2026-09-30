using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// The description cache measured as a cache (ADR-0122): what it holds, what
/// drops it, and what it costs when it is off. Every case is free of Npgsql and
/// of a container — the cache's whole input is a dataset name, a description
/// and a clock — and the store's own use of it is proved against a real
/// database in <c>PostgisDescriptionCacheTests</c>.
/// </summary>
public sealed class PostgisDescriptionCacheTests
{
    private static PostgisDatasetName Dataset(string table)
    {
        Assert.True(PostgisDatasetName.TryParse($"public.{table}", out var name, out var reason), reason);
        return name;
    }

    private static DatasetDescription Description(string id, long estimatedRows = 0) => new(
        id,
        "public",
        "places",
        "geom",
        4326,
        "Point",
        estimatedRows,
        ["id"],
        new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64),
            new FieldDefinition("geom", AttributeKind.Geometry),
        ]));

    /// <summary>
    /// The facts one discovery run holds: the description the contract carries
    /// and the collations the columns declare themselves (ADR-0136).
    /// </summary>
    private static PostgisDatasetFacts Facts(string id, IReadOnlyDictionary<string, string>? collations = null, long estimatedRows = 0) =>
        new(Dataset("places"), Description(id, estimatedRows), collations ?? new Dictionary<string, string>());

    private static MovableClock Clock() => new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>A description read once is served again without going anywhere.</summary>
    [Fact]
    public void A_held_description_is_served_until_it_is_dropped()
    {
        var clock = Clock();
        var cache = new PostgisDescriptionCache(TimeSpan.FromMinutes(1), clock);
        var name = Dataset("places");
        cache.Set(name, Facts("public.places"));

        Assert.True(cache.TryGet(name, out var served));
        Assert.Equal("public.places", served.Description.Id);
        Assert.Equal(1, cache.Count);
    }

    /// <summary>
    /// The collations the columns declare are held with the description and
    /// dropped with it (ADR-0136): a collation that outlived the schema it was
    /// read for would answer for a table that is no longer there.
    /// </summary>
    [Fact]
    public void The_column_collations_are_held_with_the_description_they_were_read_for()
    {
        var cache = new PostgisDescriptionCache(TimeSpan.FromMinutes(1), Clock());
        var name = Dataset("places");
        cache.Set(name, Facts("public.places", new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = "de-x-icu" }));

        Assert.True(cache.TryGet(name, out var served));
        Assert.Equal("de-x-icu", served.TextCollations["name"]);

        cache.Invalidate(name);

        Assert.False(cache.TryGet(name, out _));
    }

    /// <summary>A dataset the store has not described is a miss, not a guess.</summary>
    [Fact]
    public void A_dataset_that_was_never_described_is_a_miss()
    {
        var cache = new PostgisDescriptionCache(TimeSpan.FromMinutes(1), Clock());

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
        var cache = new PostgisDescriptionCache(TimeSpan.FromMinutes(1), clock);
        var name = Dataset("places");
        cache.Set(name, Facts("public.places"));

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
        var cache = new PostgisDescriptionCache(TimeSpan.FromMinutes(1), Clock());
        cache.Set(Dataset("places"), Facts("public.places"));
        cache.Set(Dataset("roads"), Facts("public.roads"));

        cache.Invalidate(Dataset("places"));

        Assert.False(cache.TryGet(Dataset("places"), out _));
        Assert.True(cache.TryGet(Dataset("roads"), out _));
        Assert.Equal(1, cache.Count);
    }

    /// <summary>Invalidating an unknown dataset is a no-op, not a failure.</summary>
    [Fact]
    public void Invalidating_a_dataset_the_store_never_described_does_nothing()
    {
        var cache = new PostgisDescriptionCache(TimeSpan.FromMinutes(1), Clock());
        cache.Set(Dataset("places"), Facts("public.places"));

        cache.Invalidate(Dataset("never_seen"));
        cache.InvalidateAll();

        Assert.False(cache.TryGet(Dataset("places"), out _));
        Assert.Equal(0, cache.Count);
    }

    /// <summary>
    /// The cache switched off: the store's behaviour before ADR-0122, and the
    /// setting for a database whose schema moves on a schedule it cannot see.
    /// Nothing is held and every read is a miss.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_window_holds_nothing(int seconds)
    {
        var cache = new PostgisDescriptionCache(TimeSpan.FromSeconds(seconds), Clock());
        var name = Dataset("places");
        cache.Set(name, Facts("public.places"));

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
        var cache = new PostgisDescriptionCache(TimeSpan.FromMinutes(1), Clock());
        var name = Dataset("places");

        cache.NoteRead();
        cache.Set(name, Facts("public.places"));
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
