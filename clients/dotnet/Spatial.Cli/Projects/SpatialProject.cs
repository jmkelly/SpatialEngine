namespace Spatial.Cli;

/// <summary>
/// The declarative spatial project document (ADR-0052): the versioned list of
/// datasets a workspace wants ingested and the maps (publications) built from
/// them. It is data, not code, so the same document can be hand-written, LLM
/// generated or produced by <c>project export</c>.
/// </summary>
public sealed record SpatialProject(
    int Version,
    IReadOnlyList<ProjectDataset> Datasets,
    IReadOnlyList<ProjectMap> Maps);

/// <summary>One dataset the project wants to exist, with its ingest parameters (ADR-0041).</summary>
public sealed record ProjectDataset(
    string Dataset,
    int Srid,
    string? Source,
    string Format = "geojson",
    string? Identity = null,
    string? IdentityField = null,
    int? SourceSrid = null);

/// <summary>One publication the project wants to exist; <c>kind</c> selects the GeoServices projection.</summary>
public sealed record ProjectMap(
    string Name,
    string Kind,
    string Store = "memory",
    string? Description = null,
    string? Copyright = null,
    IReadOnlyList<ProjectLayer>? Layers = null);

/// <summary>One ordered layer of a <see cref="ProjectMap"/>: a dataset, a geometry family and an optional style.</summary>
public sealed record ProjectLayer(
    string Dataset,
    string? Name = null,
    string Geometry = "mixed",
    ProjectStyle? Style = null);

/// <summary>
/// The compact, human/LLM-authored draw recipe inside a project file
/// (ADR-0052). It mirrors <see cref="DrawRecipe"/> one-to-one; the two convert
/// without loss.
/// </summary>
public sealed record ProjectStyle(
    string Color = "#4fc3f7",
    double Opacity = 1,
    double LineWidth = 2,
    double Radius = 5,
    bool Visible = true)
{
    /// <summary>Converts this project style to the CLI's compact draw recipe.</summary>
    public DrawRecipe ToRecipe() => new(Color, Opacity, LineWidth, Radius, Visible);

    /// <summary>Builds a project style from a compact draw recipe.</summary>
    public static ProjectStyle FromRecipe(DrawRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        return new ProjectStyle(recipe.Color, recipe.Opacity, recipe.LineWidth, recipe.Radius, recipe.Visible);
    }
}
