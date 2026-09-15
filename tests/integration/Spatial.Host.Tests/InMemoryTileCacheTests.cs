using Spatial.Host.TileServing;
using Spatial.PluginSdk;

namespace Spatial.Host.Tests;

/// <summary>
/// The initial in-memory tile cache (ADR-0046): content-addressed get/set,
/// byte and entry bounds, least-recently-used eviction, explicit removal and
/// cancellation.
/// </summary>
public sealed class InMemoryTileCacheTests
{
    private static readonly RasterFormat Format = RasterFormat.Png;

    [Fact]
    public async Task TryGet_misses_before_a_set_and_hits_after()
    {
        var cache = Cache(maxBytes: 1024, maxEntries: 4);
        var key = Key(0);
        var image = Image(10);

        Assert.Null(await cache.TryGetAsync(key));
        await cache.SetAsync(key, image);
        Assert.Same(image, await cache.TryGetAsync(key));
    }

    [Fact]
    public async Task Entries_are_content_addressed_by_version_and_format()
    {
        var cache = Cache(maxBytes: 1024, maxEntries: 4);
        await cache.SetAsync(Key(0), Image(10));

        Assert.Null(await cache.TryGetAsync(Key(1)));
        Assert.Null(await cache.TryGetAsync(Key(0) with { Format = RasterFormat.Jpeg }));
        Assert.Null(await cache.TryGetAsync(Key(0) with { Z = 1, X = 0, Y = 1 }));
    }

    [Fact]
    public async Task Setting_the_same_key_twice_replaces_the_entry()
    {
        var cache = Cache(maxBytes: 1024, maxEntries: 4);
        var key = Key(0);
        await cache.SetAsync(key, Image(10));
        var replacement = Image(20);

        await cache.SetAsync(key, replacement);

        Assert.Equal(1, cache.Count);
        Assert.Same(replacement, await cache.TryGetAsync(key));
    }

    [Fact]
    public async Task Entry_bound_evicts_the_oldest_entry()
    {
        var cache = Cache(maxBytes: 1024, maxEntries: 2);
        await cache.SetAsync(Key(0), Image(10));
        await cache.SetAsync(Key(1), Image(10));
        await cache.SetAsync(Key(2), Image(10));

        Assert.Equal(2, cache.Count);
        Assert.Null(await cache.TryGetAsync(Key(0)));
        Assert.NotNull(await cache.TryGetAsync(Key(1)));
        Assert.NotNull(await cache.TryGetAsync(Key(2)));
    }

    [Fact]
    public async Task A_recent_read_protects_an_entry_from_eviction()
    {
        var cache = Cache(maxBytes: 1024, maxEntries: 2);
        await cache.SetAsync(Key(0), Image(10));
        await cache.SetAsync(Key(1), Image(10));
        _ = await cache.TryGetAsync(Key(0));

        await cache.SetAsync(Key(2), Image(10));

        Assert.NotNull(await cache.TryGetAsync(Key(0)));
        Assert.Null(await cache.TryGetAsync(Key(1)));
        Assert.NotNull(await cache.TryGetAsync(Key(2)));
    }

    [Fact]
    public async Task Byte_bound_evicts_until_the_budget_is_met()
    {
        var cache = Cache(maxBytes: 25, maxEntries: 10);
        await cache.SetAsync(Key(0), Image(10));
        await cache.SetAsync(Key(1), Image(10));
        await cache.SetAsync(Key(2), Image(10));

        Assert.Equal(2, cache.Count);
        Assert.Null(await cache.TryGetAsync(Key(0)));
    }

    [Fact]
    public async Task An_oversized_image_is_not_cached()
    {
        var cache = Cache(maxBytes: 5, maxEntries: 10);

        await cache.SetAsync(Key(0), Image(10));

        Assert.Equal(0, cache.Count);
        Assert.Null(await cache.TryGetAsync(Key(0)));
    }

    [Fact]
    public async Task A_non_positive_bound_disables_caching()
    {
        var cache = Cache(maxBytes: 0, maxEntries: 10);

        await cache.SetAsync(Key(0), Image(10));

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Remove_reports_presence_and_frees_the_entry()
    {
        var cache = Cache(maxBytes: 1024, maxEntries: 4);
        await cache.SetAsync(Key(0), Image(10));

        Assert.True(await cache.RemoveAsync(Key(0)));
        Assert.False(await cache.RemoveAsync(Key(0)));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Clear_empties_the_cache()
    {
        var cache = Cache(maxBytes: 1024, maxEntries: 4);
        await cache.SetAsync(Key(0), Image(10));
        await cache.SetAsync(Key(1), Image(10));

        await cache.ClearAsync();

        Assert.Equal(0, cache.Count);
        Assert.Null(await cache.TryGetAsync(Key(0)));
    }

    [Fact]
    public async Task Operations_honour_cancellation()
    {
        var cache = Cache(maxBytes: 1024, maxEntries: 4);
        var canceled = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.TryGetAsync(Key(0), canceled).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SetAsync(Key(0), Image(10), canceled).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.RemoveAsync(Key(0), canceled).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.ClearAsync(canceled).AsTask());
    }

    private static InMemoryTileCache Cache(long maxBytes, int maxEntries) =>
        new(new TileCacheOptions { MaxBytes = maxBytes, MaxEntries = maxEntries });

    private static TileCacheKey Key(int y) => new("webmercator", 3, 1, y, Format, "v1");

    private static RasterImage Image(int length) =>
        new(new byte[length], "image/png", 1, 1, RasterFormat.Png);
}
