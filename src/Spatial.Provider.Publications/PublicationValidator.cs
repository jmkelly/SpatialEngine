using System.Text.RegularExpressions;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.Publications;

/// <summary>
/// Pure validation and normalisation of a <see cref="Publication"/>
/// (ADR-0041 §2). Publication names are flat and identifier-shaped (folder
/// syntax is rejected in phase one); layer datasets use the strict
/// <c>schema.table</c> grammar; layer ids are unique and non-negative. A
/// negative layer id means "assign the next free id", which is how the host
/// and the Esri admin projection create publications without hard-coding ids.
/// </summary>
internal static class PublicationValidator
{
    private static readonly Regex NamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex DatasetPattern = new(@"^[a-z_][a-z0-9_]*\.[a-z_][a-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>Validates and normalises a publication, assigning any negative layer ids.</summary>
    public static Publication Normalize(Publication publication, int nextLayerId)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ValidateHeader(publication);
        return publication with { Layers = NormalizeLayers(publication, nextLayerId) };
    }

    private static void ValidateHeader(Publication publication)
    {
        if (!NamePattern.IsMatch(publication.Name ?? string.Empty))
        {
            throw SpatialException.BadArguments(
                $"Publication name '{publication.Name}' is invalid: expected a flat identifier [A-Za-z_][A-Za-z0-9_]* (folders are not supported).");
        }

        if (string.IsNullOrWhiteSpace(publication.Store))
        {
            throw SpatialException.BadArguments($"Publication '{publication.Name}' needs a non-empty store.");
        }

        if (!Enum.IsDefined(publication.Kind))
        {
            throw SpatialException.BadArguments($"Publication '{publication.Name}' has unknown kind '{publication.Kind}'.");
        }

        if (publication.Layers.Count == 0)
        {
            throw SpatialException.BadArguments($"Publication '{publication.Name}' must expose at least one layer.");
        }
    }

    private static List<PublicationLayer> NormalizeLayers(Publication publication, int nextLayerId)
    {
        var layers = new List<PublicationLayer>(publication.Layers.Count);
        var ids = new HashSet<int>();
        var datasets = new HashSet<string>(StringComparer.Ordinal);
        var next = nextLayerId;
        foreach (var layer in publication.Layers)
        {
            ValidateLayer(publication, layer, datasets);
            layers.Add(layer with { LayerId = AssignId(publication, layer, ids, ref next) });
        }

        return layers;
    }

    private static void ValidateLayer(Publication publication, PublicationLayer layer, HashSet<string> datasets)
    {
        var dataset = layer.Dataset;
        if (string.IsNullOrEmpty(dataset) || !DatasetPattern.IsMatch(dataset))
        {
            throw SpatialException.BadArguments(
                $"Layer dataset '{dataset}' is invalid: expected schema.table with only [a-z0-9_].");
        }

        if (!datasets.Add(dataset))
        {
            throw SpatialException.BadArguments($"Publication '{publication.Name}' lists dataset '{dataset}' more than once.");
        }

        if (layer.Name is { Length: 0 })
        {
            throw SpatialException.BadArguments($"Publication '{publication.Name}' has a layer with an empty name.");
        }
    }

    private static int AssignId(Publication publication, PublicationLayer layer, HashSet<int> ids, ref int next)
    {
        var id = layer.LayerId < 0 ? next++ : layer.LayerId;
        if (!ids.Add(id))
        {
            throw SpatialException.BadArguments($"Publication '{publication.Name}' assigns layer id {id} more than once.");
        }

        return id;
    }

    /// <summary>Whether a name is a valid flat publication name.</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);
}
