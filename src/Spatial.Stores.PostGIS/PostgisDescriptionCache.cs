using System.Collections.Concurrent;
using Spatial.Contracts.Providers;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS;

/// <summary>
/// The descriptions a <see cref="PostgisStore"/> has discovered, keyed by
/// dataset (ADR-0122). Every read face describes its dataset before it can
/// compile any SQL — the schema, the identity columns, the SRID and the
/// declared layout all come from the description — and describing runs five
/// catalogue queries, so a walk of <c>N</c> pages over a layer paid <c>N</c>
/// of them to learn the same schema <c>N</c> times. The cache turns that into
/// one read per dataset per lifetime-of-the-entry.
///
/// <para>
/// It is an optimisation and never an answer: an entry is dropped by every
/// write path the store owns (create, ingest, append, edit, and the end of a
/// transaction), and it expires on its own so a schema change made outside the
/// store is picked up rather than inherited. Nothing here can turn a correct
/// read into a wrong one — the worst a stale entry can do is describe a
/// dataset as it was, which the read then reports as such.
/// </para>
/// </summary>
/// <param name="ttl">
/// How long an entry stays usable. A non-positive value turns the cache off
/// altogether, which is the store's pre-ADR-0122 behaviour (every read
/// describes) and the operator's escape hatch for a database whose schema is
/// changed out of band on a schedule this store cannot see.
/// </param>
/// <param name="clock">The clock the expiry is measured on, so a test can move time rather than sleep.</param>
internal sealed class PostgisDescriptionCache(TimeSpan ttl, TimeProvider clock)
{
    private readonly ConcurrentDictionary<PostgisDatasetName, Entry> _entries = new();
    private long _reads;

    /// <summary>Whether an entry is kept at all; false is the cache switched off.</summary>
    public bool Enabled => ttl > TimeSpan.Zero;

    /// <summary>How many descriptions are held right now — the store's invalidation diagnostic.</summary>
    public int Count => _entries.Count;

    /// <summary>How many catalogue description reads this store has issued, cached or not.</summary>
    public long Reads => Interlocked.Read(ref _reads);

    /// <summary>
    /// The description read for this dataset, when one is held and still fresh.
    /// An expired entry is dropped on the way past, so the caller that misses
    /// here is the one that re-reads it and the entry is not looked at twice.
    /// </summary>
    public bool TryGet(PostgisDatasetName name, out PostgisDatasetFacts facts)
    {
        if (!Enabled)
        {
            facts = default;
            return false;
        }

        if (_entries.TryGetValue(name, out var entry) && !entry.Expired(clock.GetUtcNow(), ttl))
        {
            facts = entry.Facts;
            return true;
        }

        _entries.TryRemove(name, out _);
        facts = default;
        return false;
    }

    /// <summary>Holds a freshly discovered description, stamped for the expiry.</summary>
    public void Set(PostgisDatasetName name, PostgisDatasetFacts facts)
    {
        if (Enabled)
        {
            _entries[name] = new Entry(facts, clock.GetUtcNow());
        }
    }

    /// <summary>Drops one dataset's description: its table changed under this store.</summary>
    public void Invalidate(PostgisDatasetName name) => _entries.TryRemove(name, out _);

    /// <summary>Drops every description: no longer used, but the honest answer for "forget everything".</summary>
    public void InvalidateAll() => _entries.Clear();

    /// <summary>Records a description that went to the catalogue, so the cost of a walk is measurable.</summary>
    public long NoteRead() => Interlocked.Increment(ref _reads);

    /// <summary>One held description and when it was discovered.</summary>
    private readonly record struct Entry(PostgisDatasetFacts Facts, DateTimeOffset ReadAt)
    {
        public bool Expired(DateTimeOffset now, TimeSpan ttl) => now - ReadAt >= ttl;
    }
}
