using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Spatial.PluginHost.DotNet.Manifest;

/// <summary>
/// Loads and validates a plugin package's <c>manifest.json</c> from its
/// package directory (a folder carrying the manifest plus the loadable
/// payload). Used by discovery (supervisor side) and by the worker host when
/// it starts — both sides must agree on the same validated schema.
/// </summary>
public static class PluginManifestLoader
{
    /// <summary>Loads and validates the manifest for <paramref name="packageDirectory"/>; throws on any problem.</summary>
    public static PluginManifest Load(string packageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        var manifestPath = Path.Combine(packageDirectory, PluginManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            throw new PluginManifestException(
                $"'{packageDirectory}' is not a plugin package: it has no {PluginManifest.FileName}.");
        }

        ManifestJson document;
        try
        {
            document = JsonSerializer.Deserialize<ManifestJson>(
                File.ReadAllText(manifestPath), JsonOptions) ?? throw new PluginManifestException(
                    $"'{manifestPath}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new PluginManifestException($"'{manifestPath}' is not valid JSON: {exception.Message}", exception);
        }

        var manifest = FromDocument(document, manifestPath);
        PluginManifestValidator.EnsureValid(manifest);
        return manifest;
    }

    /// <summary>
    /// Loads without throwing: false plus the problems when the directory is
    /// not a valid package. Discovery uses this so one bad package never
    /// aborts a directory scan.
    /// </summary>
    public static bool TryLoad(
        string packageDirectory,
        [NotNullWhen(true)] out PluginManifest? manifest,
        out IReadOnlyList<string> problems)
    {
        try
        {
            manifest = Load(packageDirectory);
            problems = [];
            return true;
        }
        catch (PluginManifestException exception)
        {
            manifest = null;
            problems = [exception.Message];
            return false;
        }
    }

    private static PluginManifest FromDocument(ManifestJson document, string path)
    {
        var capabilities = (document.Capabilities ?? []).Select(MapCapability).ToArray();
        return new PluginManifest(
            document.SchemaVersion ?? 0,
            document.Id ?? string.Empty,
            document.DisplayName ?? document.Id ?? string.Empty,
            document.Runtime ?? string.Empty,
            document.Assembly ?? string.Empty,
            document.AssemblyType,
            capabilities);
    }

    private static ManifestCapability MapCapability(ManifestCapabilityJson capability) =>
        new(
            capability.Id ?? string.Empty,
            capability.Purpose ?? string.Empty,
            capability.InputSchema ?? string.Empty,
            capability.OutputSchema ?? string.Empty,
            (capability.Errors ?? []).Select(
                error => new ManifestError(error.Code ?? string.Empty, error.Description ?? string.Empty)).ToArray(),
            capability.Permissions ?? [],
            capability.Traits ?? [],
            (capability.Examples ?? []).Select(
                example => new ManifestExample(example.Name ?? string.Empty, example.Description ?? string.Empty)).ToArray());

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
