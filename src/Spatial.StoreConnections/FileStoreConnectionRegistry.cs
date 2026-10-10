using System.Text.Json;
using System.Text.RegularExpressions;
using Spatial.Contracts;
using Spatial.Contracts.Providers;

namespace Spatial.StoreConnections;

/// <summary>
/// The file-backed <see cref="IStoreConnectionRegistry"/>: one runtime
/// connection string per store key in a protected JSON file, written
/// atomically (temp file plus rename) and created owner-only on Unix. Reads
/// resolve from memory under a mutex, so the stores' synchronous per-operation
/// lookups never touch the disk; every mutation and reload holds the same
/// mutex, so a reader never sees a half-written entry. The file is the source
/// of truth — a second host sharing it last-writer-wins and picks the change
/// up on <see cref="ReloadAsync"/> — and a missing file simply means no
/// runtime entries. Secrets are never logged; statuses never carry them.
/// </summary>
public sealed class FileStoreConnectionRegistry : IStoreConnectionRegistry
{
    private const int FileVersion = 1;

    private static readonly Regex StorePattern = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly object _mutex = new();
    private Dictionary<string, StoredConnection> _entries = new(StringComparer.Ordinal);
    private bool _loaded;

    public FileStoreConnectionRegistry(StoreConnectionOptions options, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _path = options.Path;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public bool TryGetConnectionString(string store, out string? connectionString)
    {
        lock (_mutex)
        {
            EnsureLoaded();
            if (_entries.TryGetValue(Normalise(store), out var entry)
                && !string.IsNullOrWhiteSpace(entry.ConnectionString))
            {
                connectionString = entry.ConnectionString;
                return true;
            }

            connectionString = null;
            return false;
        }
    }

    /// <inheritdoc />
    public Task<StoreConnectionStatus> SetAsync(
        string store, string connectionString, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(connectionString);
        var name = RequireStoreName(store);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw SpatialException.BadArguments($"The connection string for store '{name}' must not be empty.");
        }

        lock (_mutex)
        {
            EnsureLoaded();
            _entries[name] = new StoredConnection(connectionString, _clock.GetUtcNow());
            Persist();
            return Task.FromResult(Status(name));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoreConnectionStatus>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_mutex)
        {
            EnsureLoaded();
            return Task.FromResult<IReadOnlyList<StoreConnectionStatus>>(Statuses());
        }
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string store, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = RequireStoreName(store);
        lock (_mutex)
        {
            EnsureLoaded();
            if (!_entries.Remove(name))
            {
                return Task.FromResult(false);
            }

            Persist();
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoreConnectionStatus>> ReloadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_mutex)
        {
            _loaded = false;
            EnsureLoaded();
            return Task.FromResult<IReadOnlyList<StoreConnectionStatus>>(Statuses());
        }
    }

    private static string RequireStoreName(string? store)
    {
        var name = (store ?? string.Empty).Trim().ToLowerInvariant();
        return StorePattern.IsMatch(name)
            ? name
            : throw SpatialException.BadArguments(
                $"Store '{store}' is invalid: expected a lowercase identifier [a-z][a-z0-9_]* such as 'postgis'.");
    }

    private static string Normalise(string? store) => (store ?? string.Empty).Trim().ToLowerInvariant();

    private StoreConnectionStatus Status(string store) =>
        new(store, true, _entries[store].UpdatedAt);

    private List<StoreConnectionStatus> Statuses() =>
        _entries.Keys.OrderBy(name => name, StringComparer.Ordinal).Select(Status).ToList();

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!File.Exists(_path))
        {
            _entries = new Dictionary<string, StoredConnection>(StringComparer.Ordinal);
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(_path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SpatialException.Unavailable(
                $"The store-connection file '{_path}' cannot be read: {exception.Message}", exception);
        }

        ConnectionFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ConnectionFile>(text, Json);
        }
        catch (JsonException exception)
        {
            throw SpatialException.Unavailable(
                $"The store-connection file '{_path}' is not valid JSON: {exception.Message}", exception);
        }

        var loaded = new Dictionary<string, StoredConnection>(StringComparer.Ordinal);
        foreach (var pair in file?.Connections ?? new Dictionary<string, StoredConnection>())
        {
            var name = Normalise(pair.Key);
            if (StorePattern.IsMatch(name) && !string.IsNullOrWhiteSpace(pair.Value?.ConnectionString))
            {
                loaded[name] = new StoredConnection(pair.Value.ConnectionString, pair.Value.UpdatedAt);
            }
        }

        _entries = loaded;
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = new ConnectionFile
        {
            Version = FileVersion,
            Connections = _entries.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        };
        var temp = _path + $".{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(document, Json));
            Protect(temp);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SpatialException.Unavailable(
                $"The store-connection file '{_path}' cannot be written: {exception.Message}", exception);
        }
        finally
        {
            if (File.Exists(temp) && !string.Equals(temp, _path, StringComparison.Ordinal))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static void Protect(string temp)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private sealed record StoredConnection(string ConnectionString, DateTimeOffset UpdatedAt);

    private sealed class ConnectionFile
    {
        public int Version { get; set; } = FileVersion;

        public Dictionary<string, StoredConnection> Connections { get; set; } = new(StringComparer.Ordinal);
    }
}
