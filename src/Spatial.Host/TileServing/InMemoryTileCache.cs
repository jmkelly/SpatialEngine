using Spatial.Contracts;

namespace Spatial.Host.TileServing;

/// <summary>
/// The initial <see cref="ITileCache"/> (ADR-0046): process-memory storage
/// bounded by total bytes and entry count, evicting the least-recently-used
/// tile first. Ownership is deliberately local to the host for now; a
/// filesystem or object-store cache is a sibling implementation of the same
/// contract and changes only the DI registration.
/// </summary>
internal sealed class InMemoryTileCache : ITileCache
{
    private readonly object _gate = new();
    private readonly Dictionary<TileCacheKey, Entry> _entries = [];
    private readonly LinkedList<TileCacheKey> _order = new();
    private readonly long _maxBytes;
    private readonly int _maxEntries;
    private long _bytes;

    public InMemoryTileCache(TileCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxBytes = Math.Max(0, options.MaxBytes);
        _maxEntries = Math.Max(0, options.MaxEntries);
    }

    /// <summary>Number of cached entries (diagnostics and tests).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<RasterImage?> TryGetAsync(TileCacheKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return ValueTask.FromResult<RasterImage?>(null);
            }

            Touch(entry);
            return ValueTask.FromResult<RasterImage?>(entry.Image);
        }
    }

    /// <inheritdoc />
    public ValueTask SetAsync(TileCacheKey key, RasterImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanStore(image))
        {
            return ValueTask.CompletedTask;
        }

        lock (_gate)
        {
            RemoveEntry(key);
            _entries[key] = new Entry(image, _order.AddFirst(key));
            _bytes += SizeOf(image);
            Evict();
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(TileCacheKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(RemoveEntry(key));
        }
    }

    /// <inheritdoc />
    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _entries.Clear();
            _order.Clear();
            _bytes = 0;
        }

        return ValueTask.CompletedTask;
    }

    private bool CanStore(RasterImage image) =>
        _maxEntries > 0 && _maxBytes > 0 && SizeOf(image) <= _maxBytes;

    private bool RemoveEntry(TileCacheKey key)
    {
        if (!_entries.Remove(key, out var removed))
        {
            return false;
        }

        _order.Remove(removed.Order);
        _bytes -= SizeOf(removed.Image);
        return true;
    }

    private void Touch(Entry entry)
    {
        _order.Remove(entry.Order);
        _order.AddFirst(entry.Order);
    }

    private void Evict()
    {
        while ((_bytes > _maxBytes || _entries.Count > _maxEntries) && _order.Last is { } oldest)
        {
            _order.RemoveLast();
            if (_entries.Remove(oldest.Value, out var entry))
            {
                _bytes -= SizeOf(entry.Image);
            }
        }
    }

    private static long SizeOf(RasterImage image) => image.Content.LongLength;

    private sealed record Entry(RasterImage Image, LinkedListNode<TileCacheKey> Order);
}
