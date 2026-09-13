using System.Text;
using Spatial.Client;
using Spatial.PluginSdk.Providers;

namespace Spatial.Cli;

/// <summary>What <c>project plan</c>/<c>apply</c> would do to one dataset.</summary>
public sealed record ProjectDatasetPlan(string Dataset, bool Exists, bool WillIngest);

/// <summary>What <c>project plan</c>/<c>apply</c> would do to one map.</summary>
public sealed record ProjectMapPlan(string Name, string Kind, string Store, int Layers);

/// <summary>The machine-readable result of planning or applying a project (ADR-0052).</summary>
public sealed record ProjectPlan(IReadOnlyList<ProjectDatasetPlan> Datasets, IReadOnlyList<ProjectMapPlan> Maps);

/// <summary>
/// Turns a loaded <see cref="SpatialProject"/> into host operations
/// (ADR-0052): plan without mutating, apply idempotently (reusing existing
/// datasets and preserving stable layer ids), and export the host's current
/// state back into a project. All host access flows through
/// <see cref="ISpatialGateway"/>; this type touches no provider.
/// </summary>
public static class ProjectApplier
{
    /// <summary>Builds the plan for a project by reading (never mutating) the host.</summary>
    public static async Task<ProjectPlan> PlanAsync(
        ISpatialGateway gateway,
        SpatialProject project,
        CliSettings settings,
        bool force,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);
        var datasets = await gateway.ListDatasetsAsync(settings.Store, null, cancellationToken);
        return BuildPlan(project, datasets, force);
    }

    /// <summary>Applies a project, ingesting missing datasets and publishing every map; requires the admin token when it will mutate.</summary>
    public static async Task<ProjectPlan> ApplyAsync(
        ISpatialGateway gateway,
        SpatialProject project,
        CliSettings settings,
        bool force,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(settings);
        var datasets = await gateway.ListDatasetsAsync(settings.Store, null, cancellationToken);
        var publications = await gateway.ListPublicationsAsync(cancellationToken);
        var plan = BuildPlan(project, datasets, force);
        RequireAdminTokenWhenMutating(plan, settings.Token);
        await IngestDatasetsAsync(gateway, project, settings, datasets, force, cancellationToken);
        await PublishMapsAsync(gateway, project, settings, publications, cancellationToken);
        return plan;
    }

    /// <summary>Reads the host's datasets and publications into a version-1 project.</summary>
    public static async Task<SpatialProject> ExportAsync(
        ISpatialGateway gateway,
        CliSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(settings);
        var datasets = await gateway.ListDatasetsAsync(settings.Store, null, cancellationToken);
        var publications = await gateway.ListPublicationsAsync(cancellationToken);
        return new SpatialProject(
            SpatialProjectFile.CurrentVersion,
            datasets.Select(ToProjectDataset).ToArray(),
            publications.Select(ToProjectMap).ToArray());
    }

    /// <summary>Renders a plan as human-readable lines.</summary>
    public static string Describe(ProjectPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var builder = new StringBuilder();
        builder.Append("datasets:");
        if (plan.Datasets.Count == 0)
        {
            builder.Append(" none");
        }

        foreach (var dataset in plan.Datasets)
        {
            builder.Append(Environment.NewLine)
                .Append("  ").Append(dataset.WillIngest ? "ingest " : "reuse  ")
                .Append(dataset.Dataset)
                .Append(dataset.Exists ? " (exists)" : " (missing)");
        }

        builder.Append(Environment.NewLine).Append("maps:");
        if (plan.Maps.Count == 0)
        {
            builder.Append(" none");
        }

        foreach (var map in plan.Maps)
        {
            builder.Append(Environment.NewLine)
                .Append("  ").Append(map.Kind).Append(' ').Append(map.Name)
                .Append(" (").Append(map.Layers).Append(" layer(s))");
        }

        return builder.ToString();
    }

    private static ProjectPlan BuildPlan(SpatialProject project, IReadOnlyList<DatasetSummary> datasets, bool force)
    {
        var existing = datasets.Select(dataset => dataset.Id).ToHashSet(StringComparer.Ordinal);
        var datasetPlans = project.Datasets.Select(dataset =>
        {
            var exists = existing.Contains(dataset.Dataset);
            return new ProjectDatasetPlan(dataset.Dataset, exists, !exists || force);
        }).ToArray();
        var mapPlans = project.Maps
            .Select(map => new ProjectMapPlan(map.Name, map.Kind, map.Store, map.Layers?.Count ?? 0))
            .ToArray();
        return new ProjectPlan(datasetPlans, mapPlans);
    }

    private static void RequireAdminTokenWhenMutating(ProjectPlan plan, string? token)
    {
        if ((plan.Datasets.Any(dataset => dataset.WillIngest) || plan.Maps.Count > 0)
            && string.IsNullOrWhiteSpace(token))
        {
            throw new CliUsageException(
                "An admin token is required to apply a project. Set --token or SPATIAL_ADMIN_TOKEN.");
        }
    }

    private static async Task IngestDatasetsAsync(
        ISpatialGateway gateway,
        SpatialProject project,
        CliSettings settings,
        IReadOnlyList<DatasetSummary> datasets,
        bool force,
        CancellationToken cancellationToken)
    {
        var existing = datasets.Select(dataset => dataset.Id).ToHashSet(StringComparer.Ordinal);
        var directory = ProjectDirectory(settings.ProjectPath);
        foreach (var dataset in project.Datasets)
        {
            if (existing.Contains(dataset.Dataset) && !force)
            {
                continue;
            }

            var source = ResolveSource(dataset, directory);
            var upload = new IngestUpload(
                SourceFileName(source),
                dataset.Dataset,
                dataset.Srid,
                dataset.Format,
                settings.Store,
                dataset.Identity,
                dataset.IdentityField,
                null,
                dataset.SourceSrid);
            await gateway.IngestAsync(source, upload, settings.Token, cancellationToken);
        }
    }

    private static async Task PublishMapsAsync(
        ISpatialGateway gateway,
        SpatialProject project,
        CliSettings settings,
        IReadOnlyList<Publication> publications,
        CancellationToken cancellationToken)
    {
        foreach (var map in project.Maps)
        {
            var existing = publications.FirstOrDefault(publication =>
                string.Equals(publication.Name, map.Name, StringComparison.Ordinal));
            await gateway.PutPublicationAsync(BuildPublication(map, existing), settings.Token, cancellationToken);
        }
    }

    private static Publication BuildPublication(ProjectMap map, Publication? existing) => new(
        map.Name,
        ParseKind(map.Kind),
        map.Store,
        BuildLayers(map, existing),
        map.Description,
        map.Copyright);

    private static List<PublicationLayer> BuildLayers(ProjectMap map, Publication? existing)
    {
        var reused = ExistingLayerIds(existing);
        var next = NextLayerId(existing);
        var layers = new List<PublicationLayer>();
        foreach (var layer in map.Layers ?? [])
        {
            var layerId = reused.TryGetValue(layer.Dataset, out var known) ? known : next++;
            layers.Add(new PublicationLayer(layer.Dataset, layerId, layer.Name, LowerStyle(layer)));
        }

        return layers;
    }

    private static Dictionary<string, int> ExistingLayerIds(Publication? publication)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var layer in publication?.Layers ?? [])
        {
            ids.TryAdd(layer.Dataset, layer.LayerId);
        }

        return ids;
    }

    private static int NextLayerId(Publication? publication) =>
        publication?.Layers is { Count: > 0 } layers ? layers.Max(layer => layer.LayerId) + 1 : 0;

    private static string LowerStyle(ProjectLayer layer) =>
        MapLibreStyleBuilder.Lower(layer.Style?.ToRecipe() ?? new DrawRecipe(), GeometryFamilies.Parse(layer.Geometry));

    private static PublicationKind ParseKind(string kind) => kind.ToLowerInvariant() switch
    {
        "feature" => PublicationKind.Feature,
        "map" => PublicationKind.Map,
        "image" => PublicationKind.Image,
        _ => throw new CliUsageException($"Unknown map kind '{kind}'. Use feature, map or image."),
    };

    private static string ResolveSource(ProjectDataset dataset, string directory)
    {
        if (string.IsNullOrWhiteSpace(dataset.Source))
        {
            throw new CliUsageException(
                $"Dataset '{dataset.Dataset}' has no source; add one or reuse the existing dataset.");
        }

        return IsHttpUrl(dataset.Source) ? dataset.Source : Path.GetFullPath(Path.Combine(directory, dataset.Source));
    }

    private static string SourceFileName(string source)
    {
        var name = IsHttpUrl(source) && Uri.TryCreate(source, UriKind.Absolute, out var uri)
            ? Path.GetFileName(uri.LocalPath)
            : Path.GetFileName(source);
        return string.IsNullOrEmpty(name) ? "upload.geojson" : name;
    }

    private static bool IsHttpUrl(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string ProjectDirectory(string projectPath) =>
        Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? Directory.GetCurrentDirectory();

    private static ProjectDataset ToProjectDataset(DatasetSummary dataset) =>
        new(dataset.Id, dataset.Srid, Source: null);

    private static ProjectMap ToProjectMap(Publication publication) => new(
        publication.Name,
        publication.Kind.ToString().ToLowerInvariant(),
        publication.Store,
        publication.Description,
        publication.Copyright,
        publication.Layers.Select(ToProjectLayer).ToArray());

    private static ProjectLayer ToProjectLayer(PublicationLayer layer)
    {
        var described = MapLibreStyleBuilder.TryDescribe(layer.Style, out var recipe, out var family);
        return new ProjectLayer(
            layer.Dataset,
            layer.Name,
            described ? GeometryFamilies.Name(family) : "mixed",
            described ? ProjectStyle.FromRecipe(recipe) : null);
    }
}
