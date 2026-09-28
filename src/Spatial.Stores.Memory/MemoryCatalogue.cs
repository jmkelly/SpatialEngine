using System.Text.RegularExpressions;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;

namespace Spatial.Stores.Memory;

/// <summary>
/// The single-lock dataset table of the in-memory provider (ADR-0042). All
/// reads, edits, ingest and transaction snapshots go through here so a
/// concurrent request never observes a partially applied change. The state is
/// ephemeral: it exists only for the process lifetime.
/// </summary>
internal sealed class MemoryCatalogue
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MemoryDataset> _datasets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, MemoryDataset>> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, long>> _versionSnapshots = new(StringComparer.Ordinal);

    /// <summary>Runs <paramref name="action"/> under the catalog lock.</summary>
    public T WithLock<T>(Func<T> action)
    {
        lock (_gate)
        {
            return action();
        }
    }

    /// <summary>Runs <paramref name="action"/> under the catalog lock.</summary>
    public void WithLock(Action action)
    {
        lock (_gate)
        {
            action();
        }
    }

    public MemoryDataset Find(string dataset)
    {
        var id = Normalise(dataset);
        return _datasets.TryGetValue(id, out var found)
            ? found
            : throw SpatialException.Missing($"Unknown dataset '{id}'.");
    }

    public bool Contains(string dataset) => _datasets.ContainsKey(Normalise(dataset));

    public IReadOnlyList<DatasetSummary> List(string? pattern) =>
        _datasets.Values
            .Where(dataset => pattern is null || Matches(dataset.Id, pattern))
            .OrderBy(dataset => dataset.Id, StringComparer.Ordinal)
            .Select(dataset => dataset.ToSummary())
            .ToArray();

    public void Add(MemoryDataset dataset)
    {
        _datasets[dataset.Id] = dataset;
        Bump(dataset.Id);
    }

    /// <summary>The dataset's content version (ADR-0083), as an opaque token.</summary>
    public string Version(string dataset) =>
        _versions.TryGetValue(Normalise(dataset), out var version)
            ? version.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : ContentVersions.Unversioned;

    /// <summary>Moves the dataset's content version (ADR-0083) after a mutation.</summary>
    public void Bump(string dataset)
    {
        var id = Normalise(dataset);
        _versions[id] = _versions.GetValueOrDefault(id) + 1;
    }

    /// <summary>Snapshots every dataset so a rollback restores the exact prior state.</summary>
    public string Begin()
    {
        var handle = Guid.NewGuid().ToString("N");
        _snapshots[handle] = _datasets.ToDictionary(entry => entry.Key, entry => entry.Value.Clone(), StringComparer.Ordinal);
        _versionSnapshots[handle] = new Dictionary<string, long>(_versions, StringComparer.Ordinal);
        return handle;
    }

    public bool IsTransaction(string handle) => _snapshots.ContainsKey(handle);

    public bool Commit(string handle)
    {
        _versionSnapshots.Remove(handle);
        return _snapshots.Remove(handle);
    }

    public bool Rollback(string handle)
    {
        if (!_snapshots.Remove(handle, out var snapshot))
        {
            return false;
        }

        _datasets.Clear();
        foreach (var entry in snapshot)
        {
            _datasets[entry.Key] = entry.Value;
        }

        if (_versionSnapshots.Remove(handle, out var versions))
        {
            _versions.Clear();
            foreach (var entry in versions)
            {
                _versions[entry.Key] = entry.Value;
            }
        }

        return true;
    }

    private static string Normalise(string dataset) =>
        dataset.Contains('.') ? dataset : $"public.{dataset}";

    private static bool Matches(string id, string pattern)
    {
        var like = "^" + Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + "$";
        return Regex.IsMatch(id, like, RegexOptions.IgnoreCase);
    }
}
