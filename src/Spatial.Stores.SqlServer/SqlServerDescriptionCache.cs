using System.Collections.Concurrent;
using Spatial.Contracts.Providers;
using Spatial.Stores.SqlServer.Core;

namespace Spatial.Stores.SqlServer;

/// <summary>
/// The descriptions a <see cref="SqlServerStore"/> has discovered, keyed by
/// dataset (ADR-0151, the SQL Server half of ADR-0122). Every read face
/// describes its dataset before it can compile any T-SQL — the schema, the
/// identity columns and the geometry facts all come from the description — and
/// describing runs several catalogue reads, so a walk of <c>N</c> pages over a
/// layer paid <c>N</c> of them to learn the same schema <c>N</c> times. The
/// cache turns that into one read per dataset per lifetime-of-the-entry.
///
/// <para>
/// It is an optimisation and never an answer: an entry is dropped by every
/// write path the store owns (create, ingest, append, edit, and the end of a
/// transaction), and it expires on its own so a schema change made outside the
/// store is picked up rather than inherited.
/// </para>
/// </summary>
/// <param name="ttl">
/// How long an entry stays usable. A non-positive value turns the cache off
/// altogether, which is the store's pre-ADR-0151 behaviour (every read
/// describes) and the operator's escape hatch for a database whose schema is
/// changed out of band on a schedule this store cannot see.
/// </param>
/// <param name="clock">The clock the expiry is measured on, so a test can move time rather than sleep.</param>
internal sealed class SqlServerDescriptionCache(TimeSpan ttl, TimeProvider clock)
{
    private readonly ConcurrentDictionary<SqlServerDatasetName, Entry> _entries = new();
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
    public bool TryGet(SqlServerDatasetName name, out DatasetDescription description)
    {
        if (!Enabled)
        {
            description = null!;
            return false;
        }

        if (_entries.TryGetValue(name, out var entry) && !entry.Expired(clock.GetUtcNow(), ttl))
        {
            description = entry.Description;
            return true;
        }

        _entries.TryRemove(name, out _);
        description = null!;
        return false;
    }

    /// <summary>Holds a freshly discovered description, stamped for the expiry.</summary>
    public void Set(SqlServerDatasetName name, DatasetDescription description)
    {
        if (Enabled)
        {
            _entries[name] = new Entry(description, clock.GetUtcNow());
        }
    }

    /// <summary>Drops one dataset's description: its table changed under this store.</summary>
    public void Invalidate(SqlServerDatasetName name) => _entries.TryRemove(name, out _);

    /// <summary>Drops every description: no longer used, but the honest answer for "forget everything".</summary>
    public void InvalidateAll() => _entries.Clear();

    /// <summary>Records a description that went to the catalogue, so the cost of a walk is measurable.</summary>
    public long NoteRead() => Interlocked.Increment(ref _reads);

    /// <summary>One held description and when it was discovered.</summary>
    private readonly record struct Entry(DatasetDescription Description, DateTimeOffset ReadAt)
    {
        public bool Expired(DateTimeOffset now, TimeSpan ttl) => now - ReadAt >= ttl;
    }
}
