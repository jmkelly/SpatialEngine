using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spatial.PluginSdk;

namespace Spatial.Host.Tiling;

/// <summary>
/// The persistent <see cref="ITileCache"/> (T-001, ADR-0046): the second cache
/// implementation behind the same pluggable contract, storing one file pair
/// per tile under a shared directory. Because the filesystem is the source of
/// truth — every read checks the directory, never a process-local index —
/// tiles survive a host restart and are shared between hosts pointing at the
/// same root. Eviction is least-recently-used by file write time within the
/// configured byte and entry bounds; a non-positive bound disables caching.
/// Storage failures surface as <c>store.unavailable</c>, never raw
/// <see cref="IOException"/>s, so the tile routes keep their 503 mapping.
/// </summary>
internal sealed class FileTileCache : ITileCache
{
    private readonly string _root;
    private readonly long _maxBytes;
    private readonly int _maxEntries;

    public FileTileCache(TileCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Root))
        {
            throw SpatialException.BadArguments("The file tile cache requires Spatial:Tiles:Cache:Root.");
        }

        _root = options.Root;
        _maxBytes = Math.Max(0, options.MaxBytes);
        _maxEntries = Math.Max(0, options.MaxEntries);
    }

    /// <inheritdoc />
    public async ValueTask<RasterImage?> TryGetAsync(TileCacheKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (dataPath, metaPath) = Paths(key);
        byte[] content;
        EntryMetadata metadata;
        try
        {
            content = await File.ReadAllBytesAsync(dataPath, cancellationToken).ConfigureAwait(false);
            var meta = await File.ReadAllBytesAsync(metaPath, cancellationToken).ConfigureAwait(false);
            metadata = JsonSerializer.Deserialize<EntryMetadata>(meta)
                ?? throw new JsonException("The tile metadata is empty.");
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException)
        {
            DeleteBestEffort(dataPath, metaPath);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Unavailable(exception);
        }

        TouchBestEffort(dataPath, metaPath);
        return new RasterImage(content, metadata.MediaType, metadata.Width, metadata.Height, metadata.Format);
    }

    /// <inheritdoc />
    public async ValueTask SetAsync(TileCacheKey key, RasterImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanStore(image))
        {
            return;
        }

        var (dataPath, metaPath) = Paths(key);
        var metadata = new EntryMetadata(image.MediaType, image.Width, image.Height, image.Format);
        try
        {
            Directory.CreateDirectory(_root);
            await WriteAtomicallyAsync(dataPath, image.Content, cancellationToken).ConfigureAwait(false);
            await WriteAtomicallyAsync(metaPath, JsonSerializer.SerializeToUtf8Bytes(metadata), cancellationToken).ConfigureAwait(false);
            Evict();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(TileCacheKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (dataPath, metaPath) = Paths(key);
        try
        {
            var removed = File.Exists(dataPath);
            File.Delete(dataPath);
            File.Delete(metaPath);
            return ValueTask.FromResult(removed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Unavailable(exception);
        }
    }

    /// <inheritdoc />
    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!Directory.Exists(_root))
            {
                return ValueTask.CompletedTask;
            }

            foreach (var path in Directory.EnumerateFiles(_root, "*.tile.*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Unavailable(exception);
        }

        return ValueTask.CompletedTask;
    }

    private bool CanStore(RasterImage image) =>
        _maxEntries > 0 && _maxBytes > 0 && image.Content.LongLength <= _maxBytes;

    private (string Data, string Meta) Paths(TileCacheKey key)
    {
        var name = Name(key);
        return (Path.Combine(_root, name + ".tile.bin"), Path.Combine(_root, name + ".tile.meta"));
    }

    private static string Name(TileCacheKey key)
    {
        var text = string.Join(
            "\n",
            key.Scheme,
            key.Z.ToString(System.Globalization.CultureInfo.InvariantCulture),
            key.X.ToString(System.Globalization.CultureInfo.InvariantCulture),
            key.Y.ToString(System.Globalization.CultureInfo.InvariantCulture),
            key.Format.ToString(),
            key.Version);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private void Evict()
    {
        var ordered = Directory.EnumerateFiles(_root, "*.tile.bin", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderBy(info => info.LastWriteTimeUtc)
            .ToList();
        var bytes = ordered.Sum(info => info.Length);
        var count = ordered.Count;
        foreach (var oldest in ordered)
        {
            if (bytes <= _maxBytes && count <= _maxEntries)
            {
                break;
            }

            bytes -= oldest.Length;
            count--;
            File.Delete(oldest.FullName);
            File.Delete(MetaFor(oldest.FullName));
        }
    }

    private static string MetaFor(string dataPath) =>
        dataPath.EndsWith(".tile.bin", StringComparison.Ordinal)
            ? dataPath[..^".bin".Length] + ".meta"
            : dataPath + ".meta";

    private static async Task WriteAtomicallyAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var staged = Path.Combine(directory, ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(staged, content, cancellationToken).ConfigureAwait(false);
            File.Move(staged, path, overwrite: true);
        }
        finally
        {
            File.Delete(staged);
        }
    }

    private static void TouchBestEffort(string dataPath, string metaPath)
    {
        // LRU recency must not turn a hit into an error on a read-only share.
        try
        {
            var now = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(dataPath, now);
            File.SetLastWriteTimeUtc(metaPath, now);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteBestEffort(string dataPath, string metaPath)
    {
        try
        {
            File.Delete(dataPath);
            File.Delete(metaPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private SpatialException Unavailable(Exception inner) =>
        SpatialException.Unavailable(FormattableString.Invariant($"The tile cache at '{_root}' is unavailable."), inner);

    private sealed record EntryMetadata(string MediaType, int Width, int Height, RasterFormat Format);
}
