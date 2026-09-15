using System.Text.RegularExpressions;
using Spatial.Core.Features;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

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

    public void Add(MemoryDataset dataset) => _datasets[dataset.Id] = dataset;

    /// <summary>Snapshots every dataset so a rollback restores the exact prior state.</summary>
    public string Begin()
    {
        var handle = Guid.NewGuid().ToString("N");
        _snapshots[handle] = _datasets.ToDictionary(entry => entry.Key, entry => entry.Value.Clone(), StringComparer.Ordinal);
        return handle;
    }

    public bool IsTransaction(string handle) => _snapshots.ContainsKey(handle);

    public bool Commit(string handle) => _snapshots.Remove(handle);

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
