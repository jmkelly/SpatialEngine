using Spatial.Contracts;

namespace Spatial.Spike.TileCache;

/// <summary>
/// The spike's tile cache: the host's bounded in-memory LRU (ADR-0046) with
/// hit, miss, entry and byte counters, so "how many tiles does this edit throw
/// out" is a counted observation rather than an inference from the key
/// format. The stored bytes and the eviction order are the same rules the host
/// uses, so the entry and byte figures mean what they mean in production.
///
/// <see cref="MvtTileCache"/> counts vector entries the same way; the spike
/// measures the raster path and reads the MVT cache only to show it inherits
/// the same fan-out.
/// </summary>
internal sealed class CountingCache : ICountingTileCache
{
    private readonly object _gate = new();
    private readonly Dictionary<TileCacheKey, RasterImage> _entries = [];
    private readonly LinkedList<TileCacheKey> _order = new();
    private readonly Dictionary<VectorTileCacheKey, VectorTile> _vectors = [];
    private readonly LinkedList<VectorTileCacheKey> _vectorOrder = new();
    private readonly long _maxBytes;
    private readonly int _maxEntries;
    private long _bytes;
    private long _vectorBytes;
    private long _hits;
    private long _misses;

    public CountingCache(int maxEntries, long maxBytes)
    {
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
    }

    public int Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public long Hits
    {
        get
        {
            lock (_gate)
            {
                return _hits;
            }
        }
    }

    public long Misses
    {
        get
        {
            lock (_gate)
            {
                return _misses;
            }
        }
    }

    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes + _vectorBytes;
            }
        }
    }

    public int VectorEntries
    {
        get
        {
            lock (_gate)
            {
                return _vectors.Count;
            }
        }
    }

    public void ResetCounters()
    {
        lock (_gate)
        {
            _hits = 0;
            _misses = 0;
        }
    }

    public ValueTask<RasterImage?> TryGetAsync(TileCacheKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                _hits++;
                _order.Remove(key);
                _order.AddLast(key);
                return ValueTask.FromResult<RasterImage?>(entry);
            }

            _misses++;
            return ValueTask.FromResult<RasterImage?>(null);
        }
    }

    public ValueTask SetAsync(TileCacheKey key, RasterImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        if (_maxEntries <= 0 || _maxBytes <= 0)
        {
            return ValueTask.CompletedTask;
        }

        lock (_gate)
        {
            if (_entries.Remove(key, out var existing))
            {
                _bytes -= existing.Content.LongLength;
                _order.Remove(key);
            }

            _entries[key] = image;
            _order.AddLast(key);
            _bytes += image.Content.LongLength;
            Evict();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<VectorTile?> TryGetVectorAsync(VectorTileCacheKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_vectors.GetValueOrDefault(key));
        }
    }

    public ValueTask SetVectorAsync(VectorTileCacheKey key, VectorTile tile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tile);
        cancellationToken.ThrowIfCancellationRequested();
        if (_maxEntries <= 0 || _maxBytes <= 0)
        {
            return ValueTask.CompletedTask;
        }

        lock (_gate)
        {
            if (_vectors.Remove(key, out var existing))
            {
                _vectorBytes -= existing.Content.LongLength;
                _vectorOrder.Remove(key);
            }

            _vectors[key] = tile;
            _vectorOrder.AddLast(key);
            _vectorBytes += tile.Content.LongLength;
            Evict();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> RemoveAsync(TileCacheKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var removed = _entries.Remove(key, out var image);
            if (removed && image is not null)
            {
                _bytes -= image.Content.LongLength;
            }

            _order.Remove(key);
            return ValueTask.FromResult(removed);
        }
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _entries.Clear();
            _order.Clear();
            _vectors.Clear();
            _vectorOrder.Clear();
            _bytes = 0;
            _vectorBytes = 0;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Least-recently-used by access, over both entry kinds, exactly as the host's cache evicts.</summary>
    private void Evict()
    {
        while (_bytes + _vectorBytes > _maxBytes || _entries.Count + _vectors.Count > _maxEntries)
        {
            if (_order.Last is { } oldestRaster)
            {
                var key = oldestRaster.Value;
                _order.RemoveLast();
                if (_entries.Remove(key, out var image))
                {
                    _bytes -= image.Content.LongLength;
                }

                continue;
            }

            if (_vectorOrder.Last is { } oldestVector)
            {
                var key = oldestVector.Value;
                _vectorOrder.RemoveLast();
                if (_vectors.Remove(key, out var tile))
                {
                    _vectorBytes -= tile.Content.LongLength;
                }

                continue;
            }

            return;
        }
    }
}
