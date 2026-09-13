using System.Text.Json;
using System.Text.Json.Serialization;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.Publications;

/// <summary>
/// The phase-one publication registry (ADR-0041 §2): declared, config-seeded
/// publications are immutable through the API; runtime publications live in a
/// versioned JSON file written atomically (temp file + replace) under a
/// single-writer lock. A whole-store declared publication resolves its layers
/// once from the backing store's dataset list and caches them, so its layer
/// ids stay stable for the process lifetime. A name collision with a declared
/// entry is <c>invalid.arguments</c>, a missing runtime name is
/// <c>not.found</c> and a corrupt file is <c>store.unavailable</c>.
/// </summary>
public sealed class PublicationRegistry : IPublicationRegistry, IDisposable
{
    /// <summary>Enumerates one store's datasets; the registry uses it to expand whole-store declared publications.</summary>
    public delegate Task<IReadOnlyList<DatasetSummary>> DatasetEnumerator(string store, CancellationToken cancellationToken);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly PublicationsOptions _options;
    private readonly DatasetEnumerator _enumerate;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Publication> _declared = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Publication> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Publication>? _runtime;

    public PublicationRegistry(PublicationsOptions options, DatasetEnumerator enumerate)
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
    public async Task<IReadOnlyList<Publication>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var runtime = await EnsureLoadedAsync(cancellationToken);
            var declared = await ResolveDeclaredAsync(cancellationToken);
            return declared.Values
                .OrderBy(publication => publication.Name, StringComparer.Ordinal)
                .Concat(runtime.Values.OrderBy(publication => publication.Name, StringComparer.Ordinal))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Publication> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw SpatialException.BadArguments("A publication name is required.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_declared.ContainsKey(name))
            {
                return (await ResolveDeclaredAsync(cancellationToken))[name];
            }

            var runtime = await EnsureLoadedAsync(cancellationToken);
            return runtime.TryGetValue(name, out var publication)
                ? publication
                : throw SpatialException.Missing($"Publication '{name}' does not exist.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Publication> PutAsync(Publication publication, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var name = publication.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw SpatialException.BadArguments("A publication name is required.");
        }

        if (_declared.ContainsKey(name))
        {
            throw SpatialException.BadArguments(
                $"Publication '{name}' is declared in configuration and cannot be replaced.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var runtime = await EnsureLoadedAsync(cancellationToken);
            var next = runtime.TryGetValue(name, out var existing)
                ? existing.Layers.Max(layer => layer.LayerId) + 1
                : 0;
            var normalised = PublicationValidator.Normalize(publication, next);
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

    private async Task<Dictionary<string, Publication>> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_runtime is not null)
        {
            return _runtime;
        }

        var loaded = new Dictionary<string, Publication>(StringComparer.OrdinalIgnoreCase);
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
                    $"The publication file '{_options.Path}' cannot be read: {exception.Message}", exception);
            }

            loaded = ParseFile(text);
        }

        return _runtime = loaded;
    }

    private Dictionary<string, Publication> ParseFile(string text)
    {
        PublicationFile? file;
        try
        {
            file = JsonSerializer.Deserialize<PublicationFile>(text, Json);
        }
        catch (JsonException exception)
        {
            throw SpatialException.Unavailable(
                $"The publication file '{_options.Path}' is not valid JSON: {exception.Message}", exception);
        }

        var loaded = new Dictionary<string, Publication>(StringComparer.OrdinalIgnoreCase);
        foreach (var publication in file?.Publications ?? [])
        {
            loaded[publication.Name] = PublicationValidator.Normalize(publication, nextLayerId: 0);
        }

        return loaded;
    }

    private async Task PersistAsync(IReadOnlyDictionary<string, Publication> runtime, CancellationToken cancellationToken)
    {
        var document = new PublicationFile(1, runtime.Values.OrderBy(publication => publication.Name, StringComparer.Ordinal).ToArray());
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

    private async Task<Dictionary<string, Publication>> ResolveDeclaredAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, publication) in _declared)
        {
            if (_resolved.ContainsKey(name))
            {
                continue;
            }

            var layers = publication.Layers.Count > 0
                ? publication.Layers
                : await EnumerateLayersAsync(publication, cancellationToken);
            _resolved[name] = publication with { Layers = layers };
        }

        return _resolved;
    }

    private async Task<IReadOnlyList<PublicationLayer>> EnumerateLayersAsync(Publication publication, CancellationToken cancellationToken)
    {
        var datasets = await _enumerate(publication.Store, cancellationToken);
        return datasets
            .OrderBy(dataset => dataset.Id, StringComparer.Ordinal)
            .Select((dataset, index) => new PublicationLayer(dataset.Id, index))
            .ToArray();
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private static Publication BuildDeclared(DeclaredPublicationOptions declared)
    {
        if (!Enum.TryParse<PublicationKind>(declared.Kind, ignoreCase: true, out var kind))
        {
            throw SpatialException.BadArguments(
                $"Declared publication '{declared.Name}' has unknown kind '{declared.Kind}'.");
        }

        var layers = declared.Layers
            .Select(layer => new PublicationLayer(layer.Dataset, layer.LayerId, layer.Name, layer.Style))
            .ToArray();
        var publication = new Publication(declared.Name, kind, declared.Store, layers, declared.Description, declared.Copyright);
        return declared.Layers.Count == 0 ? publication : PublicationValidator.Normalize(publication, nextLayerId: 0);
    }
}

/// <summary>The versioned on-disk publication document (ADR-0041 §2).</summary>
internal sealed record PublicationFile(int Version, IReadOnlyList<Publication> Publications);
