using Spatial.Host.Tiling;
using Spatial.PluginSdk;

namespace Spatial.Host.Tests;

/// <summary>
/// The persistent tile cache (T-001, ADR-0046 second <see cref="ITileCache"/>):
/// tiles written by one cache instance are readable from another instance over
/// the same directory, so tiles survive a host restart and are shared between
/// hosts. Failures are structured <see cref="SpatialException"/> codes and
/// operations honour cancellation.
/// </summary>
public sealed class FileTileCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "spatial-tiles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Tiles_survive_a_restart_and_are_shared_between_instances()
    {
        var key = new TileCacheKey("webmercator", 3, 1, 2, RasterFormat.Png, "v1");
        var image = new RasterImage([1, 2, 3, 4], "image/png", 256, 256, RasterFormat.Png);

        var first = Cache();
        await first.SetAsync(key, image);

        // A second live instance over the same directory sees the tile (shared),
        // and a fresh instance after the first is gone still does (restart).
        Assert.Equal(image.Content, (await Cache().TryGetAsync(key))?.Content);
        var revived = await Cache().TryGetAsync(key);
        Assert.Equal(image.MediaType, revived?.MediaType);
        Assert.Equal(image.Width, revived?.Width);
        Assert.Equal(image.Height, revived?.Height);
        Assert.Equal(image.Format, revived?.Format);
    }

    [Fact]
    public async Task Remove_and_clear_are_visible_to_other_instances()
    {
        var key = new TileCacheKey("webmercator", 3, 1, 2, RasterFormat.Png, "v1");
        await Cache().SetAsync(key, Image(8));
        Assert.NotNull(await Cache().TryGetAsync(key));

        Assert.True(await Cache().RemoveAsync(key));
        Assert.Null(await Cache().TryGetAsync(key));

        await Cache().SetAsync(key, Image(8));
        await Cache().ClearAsync();
        Assert.Null(await Cache().TryGetAsync(key));
    }

    [Fact]
    public void An_empty_root_is_rejected_as_invalid_arguments()
    {
        var exception = Assert.Throws<SpatialException>(() => new FileTileCache(
            new TileCacheOptions { Provider = "file", Root = " " }));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public async Task A_root_blocked_by_a_file_fails_as_store_unavailable()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "spatial-tiles-blocker-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blocker, "not a directory");
        try
        {
            var cache = new FileTileCache(new TileCacheOptions
            {
                Provider = "file",
                Root = Path.Combine(blocker, "tiles"),
            });
            var exception = await Assert.ThrowsAsync<SpatialException>(() =>
                cache.SetAsync(new TileCacheKey("webmercator", 0, 0, 0, RasterFormat.Png, "v1"), Image(4)).AsTask());

            Assert.Equal(SpatialException.StoreUnavailable, exception.Code);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task The_entry_bound_evicts_the_oldest_tile()
    {
        var cache = new FileTileCache(new TileCacheOptions
        {
            Provider = "file",
            Root = _root,
            MaxBytes = 1 << 20,
            MaxEntries = 2,
        });
        await cache.SetAsync(Key(0), Image(8));
        await Task.Delay(50);
        await cache.SetAsync(Key(1), Image(8));
        await Task.Delay(50);
        await cache.SetAsync(Key(2), Image(8));

        Assert.Null(await cache.TryGetAsync(Key(0)));
        Assert.NotNull(await cache.TryGetAsync(Key(1)));
        Assert.NotNull(await cache.TryGetAsync(Key(2)));
    }

    [Fact]
    public async Task Operations_honour_cancellation()
    {
        var cache = Cache();
        var key = new TileCacheKey("webmercator", 0, 0, 0, RasterFormat.Png, "v1");
        var canceled = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.TryGetAsync(key, canceled).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SetAsync(key, Image(4), canceled).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.RemoveAsync(key, canceled).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.ClearAsync(canceled).AsTask());
    }

    private FileTileCache Cache() => new(new TileCacheOptions
    {
        Provider = "file",
        Root = _root,
        MaxBytes = 1 << 20,
        MaxEntries = 64,
    });

    private static TileCacheKey Key(int y) =>
        new("webmercator", 3, 1, y, RasterFormat.Png, "v1");

    private static RasterImage Image(int length) =>
        new(new byte[length], "image/png", 256, 256, RasterFormat.Png);
}
