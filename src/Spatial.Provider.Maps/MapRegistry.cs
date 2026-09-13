using System.Text.Json;
using System.Text.Json.Serialization;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.Maps;

/// <summary>
/// The map registry (ADR-0052 §2, evolving ADR-0041 §2): declared,
/// config-seeded maps are immutable through the API; runtime maps live in a
/// versioned JSON file written atomically (temp file + replace) under a
/// single-writer lock. A whole-store declared map resolves its layers once
/// from the backing store's dataset list and caches them, so its layer ids
/// stay stable for the process lifetime. A pre-ADR-0052 publications file is
/// read once and migrated. A name collision with a declared entry is
/// <c>invalid.arguments</c>, a missing runtime name is <c>not.found</c> and a
/// corrupt file is <c>store.unavailable</c>.
/// </summary>
public sealed class MapRegistry : IMapRegistry, IDisposable
{
    /// <summary>Enumerates one store's datasets; the registry uses it to expand whole-store declared maps.</summary>
    public delegate Task<IReadOnlyList<DatasetSummary>> DatasetEnumerator(string store, CancellationToken cancellationToken);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly MapsOptions _options;
    private readonly DatasetEnumerator _enumerate;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Map> _declared = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Map> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Map>? _runtime;

    public MapRegistry(MapsOptions options, DatasetEnumerator enumerate)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(enumerate);
        _options = options;
        _enumerate = enumerate;
        foreach (var declared in options.Declared)
        {
            _declared[declared.Name] = BuildDeclared(declared);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Map>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var runtime = await EnsureLoadedAsync(cancellationToken);
            var declared = await ResolveDeclaredAsync(cancellationToken);
            return declared.Values
                .OrderBy(map => map.Name, StringComparer.Ordinal)
                .Concat(runtime.Values.OrderBy(map => map.Name, StringComparer.Ordinal))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Map> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw SpatialException.BadArguments("A map name is required.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_declared.ContainsKey(name))
            {
                return (await ResolveDeclaredAsync(cancellationToken))[name];
            }

            var runtime = await EnsureLoadedAsync(cancellationToken);
            return runtime.TryGetValue(name, out var map)
                ? map
                : throw SpatialException.Missing($"Map '{name}' does not exist.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Map> PutAsync(Map map, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        var name = map.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw SpatialException.BadArguments("A map name is required.");
        }

        if (_declared.ContainsKey(name))
        {
            throw SpatialException.BadArguments(
                $"Map '{name}' is declared in configuration and cannot be replaced.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var runtime = await EnsureLoadedAsync(cancellationToken);
            var next = runtime.TryGetValue(name, out var existing)
                ? existing.Layers.Max(layer => layer.LayerId) + 1
                : 0;
            var normalised = MapValidator.Normalize(map, next);
            runtime[normalised.Name] = normalised;
            await PersistAsync(runtime, cancellationToken);
            return normalised;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (_declared.ContainsKey(name))
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var runtime = await EnsureLoadedAsync(cancellationToken);
            if (!runtime.Remove(name))
            {
                return false;
            }

            await PersistAsync(runtime, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, Map>> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_runtime is not null)
        {
            return _runtime;
        }

        var loaded = new Dictionary<string, Map>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(_options.Path))
        {
            string text;
            try
            {
                text = await File.ReadAllTextAsync(_options.Path, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw SpatialException.Unavailable(
                    $"The map file '{_options.Path}' cannot be read: {exception.Message}", exception);
            }

            loaded = ParseFile(text);
        }
        else if (!string.IsNullOrWhiteSpace(_options.LegacyPath) && File.Exists(_options.LegacyPath))
        {
            loaded = await ReadLegacyAsync(_options.LegacyPath, cancellationToken);
        }

        return _runtime = loaded;
    }

    private Dictionary<string, Map> ParseFile(string text)
    {
        MapFile? file;
        try
        {
            file = JsonSerializer.Deserialize<MapFile>(text, Json);
        }
        catch (JsonException exception)
        {
            throw SpatialException.Unavailable(
                $"The map file '{_options.Path}' is not valid JSON: {exception.Message}", exception);
        }

        var loaded = new Dictionary<string, Map>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in file?.Maps ?? [])
        {
            loaded[map.Name] = MapValidator.Normalize(map, nextLayerId: 0);
        }

        return loaded;
    }

    /// <summary>Reads a pre-ADR-0052 publications file and projects each record onto a map.</summary>
    private static async Task<Dictionary<string, Map>> ReadLegacyAsync(string path, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw SpatialException.Unavailable(
                $"The legacy publication file '{path}' cannot be read: {exception.Message}", exception);
        }

        LegacyPublicationFile? file;
        try
        {
            file = JsonSerializer.Deserialize<LegacyPublicationFile>(text, Json);
        }
        catch (JsonException exception)
        {
            throw SpatialException.Unavailable(
                $"The legacy publication file '{path}' is not valid JSON: {exception.Message}", exception);
        }

        var loaded = new Dictionary<string, Map>(StringComparer.OrdinalIgnoreCase);
        foreach (var legacy in file?.Publications ?? [])
        {
            if (legacy.Layers.Count == 0)
            {
                continue;
            }

            var service = legacy.Kind?.ToLowerInvariant() switch
            {
                "feature" => MapService.Feature,
                "map" => MapService.Map,
                "image" => MapService.Image,
                _ => (MapService?)null,
            };
            if (service is null)
            {
                continue;
            }

            var layers = legacy.Layers
                .Select(layer => new MapLayer(layer.Dataset, layer.LayerId, layer.Name, layer.Style))
                .ToList();
            var map = new Map(legacy.Name, legacy.Store, layers, [service.Value], legacy.Description, legacy.Copyright);
            loaded[map.Name] = MapValidator.Normalize(map, nextLayerId: 0);
        }

        return loaded;
    }

    private async Task PersistAsync(IReadOnlyDictionary<string, Map> runtime, CancellationToken cancellationToken)
    {
        var document = new MapFile(1, runtime.Values.OrderBy(map => map.Name, StringComparer.Ordinal).ToArray());
        var json = JsonSerializer.Serialize(document, Json);
        var fullPath = System.IO.Path.GetFullPath(_options.Path);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = fullPath + ".tmp";
        await File.WriteAllTextAsync(temp, json, cancellationToken);
        File.Move(temp, fullPath, overwrite: true);
    }

    private async Task<Dictionary<string, Map>> ResolveDeclaredAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, map) in _declared)
        {
            if (_resolved.ContainsKey(name))
            {
                continue;
            }

            var layers = map.Layers.Count > 0
                ? map.Layers
                : await EnumerateLayersAsync(map, cancellationToken);
            _resolved[name] = map with { Layers = layers };
        }

        return _resolved;
    }

    private async Task<IReadOnlyList<MapLayer>> EnumerateLayersAsync(Map map, CancellationToken cancellationToken)
    {
        var datasets = await _enumerate(map.Store, cancellationToken);
        return datasets
            .OrderBy(dataset => dataset.Id, StringComparer.Ordinal)
            .Select((dataset, index) => new MapLayer(dataset.Id, index))
            .ToArray();
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private static Map BuildDeclared(DeclaredMapOptions declared)
    {
        var services = new List<MapService>();
        foreach (var name in declared.Services)
        {
            if (!Enum.TryParse<MapService>(name, ignoreCase: true, out var service) || !Enum.IsDefined(service))
            {
                throw SpatialException.BadArguments(
                    $"Declared map '{declared.Name}' has unknown service '{name}'.");
            }

            services.Add(service);
        }

        var layers = declared.Layers.Select(layer =>
        {
            if (!Enum.TryParse<MapLayerKind>(layer.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
            {
                throw SpatialException.BadArguments(
                    $"Declared map '{declared.Name}' layer '{layer.Dataset}' has unknown kind '{layer.Kind}'.");
            }

            return new MapLayer(layer.Dataset, layer.LayerId, layer.Name, layer.Style, kind, layer.Store);
        }).ToArray();

        var map = new Map(declared.Name, declared.Store, layers, services, declared.Description, declared.Copyright);
        return declared.Layers.Count == 0 ? map : MapValidator.Normalize(map, nextLayerId: 0);
    }
}

/// <summary>The versioned on-disk map document (ADR-0052 §2).</summary>
internal sealed record MapFile(int Version, IReadOnlyList<Map> Maps);

/// <summary>The pre-ADR-0052 publications document, read only for migration.</summary>
internal sealed record LegacyPublicationFile(int Version, IReadOnlyList<LegacyPublication> Publications);

/// <summary>One pre-ADR-0052 publication record.</summary>
internal sealed record LegacyPublication(
    string Name,
    string? Kind,
    string Store,
    IReadOnlyList<LegacyPublicationLayer> Layers,
    string? Description = null,
    string? Copyright = null);

/// <summary>One pre-ADR-0052 publication layer.</summary>
internal sealed record LegacyPublicationLayer(string Dataset, int LayerId, string? Name = null, string? Style = null);
