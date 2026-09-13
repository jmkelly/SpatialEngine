using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spatial.Cli;

/// <summary>
/// Reads and writes the versioned spatial project file (ADR-0052). The
/// document is camelCase JSON, indented, with comments and trailing commas
/// tolerated on read. Saving is atomic: a temp file in the same directory is
/// moved over the target, so a concurrent reader never sees a partial write.
/// </summary>
public static class SpatialProjectFile
{
    /// <summary>The only project-file version this CLI understands.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads and validates a project file, failing with a usage error when it is missing, malformed or a future version.</summary>
    public static SpatialProject Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new CliUsageException($"Project file '{path}' does not exist.");
        }

        return Validate(Deserialize(path), path);
    }

    private static SpatialProject? Deserialize(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<SpatialProject>(File.ReadAllText(path), Options);
        }
        catch (JsonException exception)
        {
            throw new CliUsageException($"Project file '{path}' is not valid JSON: {exception.Message}");
        }
    }

    private static SpatialProject Validate(SpatialProject? project, string path)
    {
        if (project is null || project.Datasets is null || project.Maps is null)
        {
            throw new CliUsageException($"Project file '{path}' is missing its 'datasets' or 'maps' array.");
        }

        if (project.Version != CurrentVersion)
        {
            throw new CliUsageException(
                $"Unsupported project version {project.Version} in '{path}'. This CLI understands version {CurrentVersion}.");
        }

        return project;
    }

    /// <summary>Writes a project file atomically, creating its directory when necessary.</summary>
    public static void Save(string path, SpatialProject project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(project);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var moved = false;
        try
        {
            File.WriteAllText(temp, Serialize(project));
            File.Move(temp, fullPath, overwrite: true);
            moved = true;
        }
        finally
        {
            if (!moved && File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>Serialises a project to the canonical JSON text.</summary>
    public static string Serialize(SpatialProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return JsonSerializer.Serialize(project, Options);
    }

    /// <summary>The starter document <c>project init</c> writes: one example dataset and one styled map.</summary>
    public static SpatialProject Starter() => new(
        CurrentVersion,
        [
            new ProjectDataset(
                "public.world_places",
                4326,
                "https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/ne_110m_populated_places.geojson",
                Identity: "none"),
        ],
        [
            new ProjectMap(
                "WorldPlaces",
                "map",
                "memory",
                "A styled reference map",
                "Natural Earth",
                [
                    new ProjectLayer(
                        "public.world_places",
                        "Places",
                        "point",
                        new ProjectStyle("#ffd54f", 0.9, Radius: 3)),
                ]),
        ]);
}
