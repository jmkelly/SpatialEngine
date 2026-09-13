using Spatial.PluginSdk.Providers;

namespace Spatial.Cli;

/// <summary>
/// The <c>map</c> command group (ADR-0052): list and inspect publications,
/// create/delete them, add/remove/style layers, and export the GeoServices
/// endpoint a publication projects to. A "map" is the neutral
/// <see cref="Publication"/>; its kind selects FeatureServer, MapServer or
/// ImageServer (ADR-0041/0048/0051).
/// </summary>
public static class MapCommands
{
    /// <summary>Catalog metadata for this group's commands.</summary>
    public static IReadOnlyList<CliCommandInfo> Specs { get; } =
    [
        new("map", "list", "List every publication", "", []),
        new("map", "show", "Show one publication with layers and styles", "<name>", []),
        new("map", "create", "Create or replace a publication", "",
        [
            new("name", "NAME", "Publication name", Required: true),
            new("kind", "feature|map|image", "Publication kind (selects the GeoServices projection)", Required: true),
            new("store", "NAME", "Source store (default memory)"),
            new("description", "TEXT", "Human description served to clients"),
            new("copyright", "TEXT", "Copyright/attribution text"),
            new("layer", "DATASET[=NAME]", "Add a layer (repeatable)"),
            new("force", "", "Replace an existing publication", IsFlag: true),
        ]),
        new("map", "delete", "Delete a runtime publication", "<name>", []),
        new("map", "add-layer", "Append a layer to a publication", "",
        [
            new("map", "NAME", "Publication name", Required: true),
            new("dataset", "schema.table", "Dataset to project", Required: true),
            new("name", "NAME", "Display name for the layer"),
        ]),
        new("map", "remove-layer", "Remove a dataset's layer from a publication", "",
        [
            new("map", "NAME", "Publication name", Required: true),
            new("dataset", "schema.table", "Dataset to remove", Required: true),
        ]),
        new("map", "set-style", "Set one layer's compact draw recipe", "",
        [
            new("map", "NAME", "Publication name", Required: true),
            new("dataset", "schema.table", "Layer's dataset", Required: true),
            new("geometry", "point|line|polygon|mixed", "Geometry family (default mixed)"),
            new("color", "#rrggbb", "Fill/line/point colour (default #4fc3f7)"),
            new("opacity", "0..1", "Layer opacity (default 1)"),
            new("line-width", "NUMBER", "Line width (default 2)"),
            new("radius", "NUMBER", "Point radius (default 5)"),
            new("hidden", "", "Hide the layer", IsFlag: true),
        ]),
        new("map", "export", "Show a publication and its GeoServices endpoint", "<name>",
        [
            new("format", "detail|url", "detail renders the publication; url prints only the endpoint (default detail)"),
        ]),
    ];

    /// <summary>Runs a <c>map</c> verb.</summary>
    public static Task<int> RunAsync(CliContext context) => context.Verb switch
    {
        "list" => ListAsync(context),
        "show" => ShowAsync(context),
        "export" => ExportAsync(context),
        "create" => CreateAsync(context),
        "delete" => DeleteAsync(context),
        "add-layer" => AddLayerAsync(context),
        "remove-layer" => RemoveLayerAsync(context),
        "set-style" => SetStyleAsync(context),
        _ => throw new CliUsageException($"Unknown command '{context.Command}'."),
    };

    private static async Task<int> ListAsync(CliContext context)
    {
        var publications = await context.Gateway.ListPublicationsAsync();
        var human = publications.Count == 0
            ? "No publications."
            : string.Join(Environment.NewLine, publications.Select(publication => publication.ToString()));
        context.Output.Result(context.Command, new { publications }, human);
        return ExitCodes.Success;
    }

    private static async Task<int> ShowAsync(CliContext context)
    {
        var name = context.Arguments.RequirePositional(0, "a publication name");
        var publication = await context.Gateway.FindPublicationAsync(name);
        if (publication is null)
        {
            context.Output.Error(context.Command, "not.found", $"No publication '{name}'.");
            return ExitCodes.NotFound;
        }

        var view = PublicationViewFactory.Describe(publication, context.Settings.Host, context.Settings.GeoServicesRoot);
        context.Output.Result(context.Command, view, PublicationViewFactory.Human(view));
        return ExitCodes.Success;
    }

    private static async Task<int> ExportAsync(CliContext context)
    {
        var name = context.Arguments.RequirePositional(0, "a publication name");
        var format = (context.Arguments.Optional("format") ?? "detail").ToLowerInvariant();
        if (format is not ("detail" or "url"))
        {
            throw new CliUsageException($"Unknown export format '{format}'. Use detail or url.");
        }

        var publication = await context.Gateway.FindPublicationAsync(name);
        if (publication is null)
        {
            context.Output.Error(context.Command, "not.found", $"No publication '{name}'.");
            return ExitCodes.NotFound;
        }

        var view = PublicationViewFactory.Describe(publication, context.Settings.Host, context.Settings.GeoServicesRoot);
        if (format == "url")
        {
            var endpoint = view.Endpoint ?? throw new CliUsageException($"Publication '{name}' has no GeoServices projection.");
            context.Output.Result(context.Command, endpoint, endpoint);
            return ExitCodes.Success;
        }

        context.Output.Result(context.Command, view, PublicationViewFactory.Human(view));
        return ExitCodes.Success;
    }

    private static Task<int> CreateAsync(CliContext context) =>
        throw new CliUsageException("'map create' is not implemented yet.");

    private static Task<int> DeleteAsync(CliContext context) =>
        throw new CliUsageException("'map delete' is not implemented yet.");

    private static Task<int> AddLayerAsync(CliContext context) =>
        throw new CliUsageException("'map add-layer' is not implemented yet.");

    private static Task<int> RemoveLayerAsync(CliContext context) =>
        throw new CliUsageException("'map remove-layer' is not implemented yet.");

    private static Task<int> SetStyleAsync(CliContext context) =>
        throw new CliUsageException("'map set-style' is not implemented yet.");
}
