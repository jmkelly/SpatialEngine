using Spatial.PluginSdk.Providers;

namespace Spatial.Cli;

/// <summary>
/// The <c>map</c> command group (ADR-0052): list and inspect maps,
/// create/delete them, add/remove/style layers, and export the GeoServices
/// endpoint a map projects to. A "map" is the neutral
/// <see cref="Map"/>; its kind selects FeatureServer, MapServer or
/// ImageServer (ADR-0041/0048/0051).
/// </summary>
public static class MapCommands
{
    /// <summary>Catalog metadata for this group's commands.</summary>
    public static IReadOnlyList<CliCommandInfo> Specs { get; } =
    [
        new("map", "list", "List every map", "", []),
        new("map", "show", "Show one map with layers and styles", "<name>", []),
        new("map", "create", "Create or replace a map", "",
        [
            new("name", "NAME", "Map name", Required: true),
            new("kind", "feature|map|image", "Map kind (selects the GeoServices projection)", Required: true),
            new("store", "NAME", "Source store (default memory)"),
            new("description", "TEXT", "Human description served to clients"),
            new("copyright", "TEXT", "Copyright/attribution text"),
            new("layer", "DATASET[=NAME]", "Add a layer (repeatable)"),
            new("force", "", "Replace an existing map", IsFlag: true),
        ]),
        new("map", "delete", "Delete a runtime map", "<name>", []),
        new("map", "add-layer", "Append a layer to a map", "",
        [
            new("map", "NAME", "Map name", Required: true),
            new("dataset", "schema.table", "Dataset to project", Required: true),
            new("name", "NAME", "Display name for the layer"),
        ]),
        new("map", "remove-layer", "Remove a dataset's layer from a map", "",
        [
            new("map", "NAME", "Map name", Required: true),
            new("dataset", "schema.table", "Dataset to remove", Required: true),
        ]),
        new("map", "set-style", "Set one layer's compact draw recipe", "",
        [
            new("map", "NAME", "Map name", Required: true),
            new("dataset", "schema.table", "Layer's dataset", Required: true),
            new("geometry", "point|line|polygon|mixed", "Geometry family (default mixed)"),
            new("color", "#rrggbb", "Fill/line/point colour (default #4fc3f7)"),
            new("opacity", "0..1", "Layer opacity (default 1)"),
            new("line-width", "NUMBER", "Positive line width (default 2)"),
            new("radius", "NUMBER", "Positive point radius (default 5)"),
            new("hidden", "", "Hide the layer", IsFlag: true),
            new("visible", "", "Show the layer (overrides --hidden)", IsFlag: true),
        ]),
        new("map", "export", "Show a map and its GeoServices endpoint", "<name>",
        [
            new("format", "detail|url", "detail renders the map; url prints only the endpoint (default detail)"),
        ]),
    ];

    private static readonly Dictionary<string, Func<CliContext, Task<int>>> Handlers = new(StringComparer.Ordinal)
    {
        ["list"] = ListAsync,
        ["show"] = ShowAsync,
        ["export"] = ExportAsync,
        ["create"] = CreateAsync,
        ["delete"] = DeleteAsync,
        ["add-layer"] = AddLayerAsync,
        ["remove-layer"] = RemoveLayerAsync,
        ["set-style"] = SetStyleAsync,
    };

    /// <summary>Runs a <c>map</c> verb.</summary>
    public static Task<int> RunAsync(CliContext context) =>
        Handlers.TryGetValue(context.Verb, out var handler)
            ? handler(context)
            : throw new CliUsageException($"Unknown command '{context.Command}'.");

    private static async Task<int> ListAsync(CliContext context)
    {
        var maps = await context.Gateway.ListMapsAsync();
        var human = maps.Count == 0
            ? "No maps."
            : string.Join(Environment.NewLine, maps.Select(map => map.ToString()));
        context.Output.Result(context.Command, new { maps }, human);
        return ExitCodes.Success;
    }

    private static async Task<int> ShowAsync(CliContext context)
    {
        var name = context.Arguments.RequirePositional(0, "a map name");
        var map = await context.Gateway.FindMapAsync(name);
        if (map is null)
        {
            context.Output.Error(context.Command, "not.found", $"No map '{name}'.");
            return ExitCodes.NotFound;
        }

        var view = MapViewFactory.Describe(map, context.Settings.Host, context.Settings.GeoServicesRoot);
        context.Output.Result(context.Command, view, MapViewFactory.Human(view));
        return ExitCodes.Success;
    }

    private static async Task<int> ExportAsync(CliContext context)
    {
        var name = context.Arguments.RequirePositional(0, "a map name");
        var format = (context.Arguments.Optional("format") ?? "detail").ToLowerInvariant();
        if (format is not ("detail" or "url"))
        {
            throw new CliUsageException($"Unknown export format '{format}'. Use detail or url.");
        }

        var map = await context.Gateway.FindMapAsync(name);
        if (map is null)
        {
            context.Output.Error(context.Command, "not.found", $"No map '{name}'.");
            return ExitCodes.NotFound;
        }

        var view = MapViewFactory.Describe(map, context.Settings.Host, context.Settings.GeoServicesRoot);
        if (format == "url")
        {
            if (view.Endpoints.Count == 0)
            {
                throw new CliUsageException($"Map '{name}' has no GeoServices projection.");
            }

            var endpoints = string.Join(Environment.NewLine, view.Endpoints);
            context.Output.Result(context.Command, endpoints, endpoints);
            return ExitCodes.Success;
        }

        context.Output.Result(context.Command, view, MapViewFactory.Human(view));
        return ExitCodes.Success;
    }

    private static async Task<int> CreateAsync(CliContext context)
    {
        var name = context.Arguments.Require("name");
        var kind = MapEditing.ParseKind(context.Arguments.Require("kind"));
        var store = context.Arguments.Optional("store") ?? context.Settings.Store;
        var requests = MapEditing.ParseLayers(context.Arguments.All("layer"));

        var existing = await context.Gateway.FindMapAsync(name);
        if (existing is not null && !context.Arguments.Has("force"))
        {
            throw new CliUsageException($"Map '{name}' already exists; pass --force to replace it.");
        }

        var map = new Map(
            name,
            store,
            MapEditing.AssignLayers(requests, existing, MapEditing.LayerKind(kind)),
            [kind],
            context.Arguments.Optional("description"),
            context.Arguments.Optional("copyright"));
        return await MapEditing.CommitAsync(context, "create", map, $"Would create map '{name}'.");
    }

    private static async Task<int> DeleteAsync(CliContext context)
    {
        var name = context.Arguments.RequirePositional(0, "a map name");
        if (context.Settings.DryRun)
        {
            context.Output.Result(context.Command, new { action = "delete", name }, $"Would delete map '{name}'.");
            return ExitCodes.Success;
        }

        var token = context.Settings.Token ?? throw new CliUsageException(MapEditing.AdminTokenRequired);
        if (!await context.Gateway.DeleteMapAsync(name, token))
        {
            return MapEditing.NotFound(context, $"No map '{name}'.");
        }

        context.Output.Result(context.Command, new { name, deleted = true }, $"Deleted map '{name}'.");
        return ExitCodes.Success;
    }

    private static async Task<int> AddLayerAsync(CliContext context)
    {
        var name = context.Arguments.Require("map");
        var dataset = context.Arguments.Require("dataset");
        var map = await context.Gateway.FindMapAsync(name);
        if (map is null)
        {
            return MapEditing.NotFound(context, $"No map '{name}'.");
        }

        if (MapEditing.FindLayer(map, dataset) is not null)
        {
            throw new CliUsageException($"Map '{name}' already has a layer for dataset '{dataset}'.");
        }

        var layer = new MapLayer(dataset, MapEditing.NextLayerId(map), context.Arguments.Optional("name"), Kind: MapEditing.PrimaryLayerKind(map));
        var updated = map with { Layers = [.. map.Layers, layer] };
        return await MapEditing.CommitAsync(context, "add-layer", updated, $"Would add layer '{dataset}' to map '{name}'.");
    }

    private static async Task<int> RemoveLayerAsync(CliContext context)
    {
        var name = context.Arguments.Require("map");
        var dataset = context.Arguments.Require("dataset");
        var map = await context.Gateway.FindMapAsync(name);
        if (map is null)
        {
            return MapEditing.NotFound(context, $"No map '{name}'.");
        }

        if (MapEditing.FindLayer(map, dataset) is null)
        {
            return MapEditing.NotFound(context, $"Map '{name}' has no layer for dataset '{dataset}'.");
        }

        var updated = map with
        {
            Layers = map.Layers
                .Where(layer => !string.Equals(layer.Dataset, dataset, StringComparison.Ordinal))
                .ToArray(),
        };
        return await MapEditing.CommitAsync(context, "remove-layer", updated, $"Would remove layer '{dataset}' from map '{name}'.");
    }

    private static async Task<int> SetStyleAsync(CliContext context)
    {
        var name = context.Arguments.Require("map");
        var dataset = context.Arguments.Require("dataset");
        var map = await context.Gateway.FindMapAsync(name);
        if (map is null)
        {
            return MapEditing.NotFound(context, $"No map '{name}'.");
        }

        var layer = MapEditing.FindLayer(map, dataset);
        if (layer is null)
        {
            return MapEditing.NotFound(context, $"Map '{name}' has no layer for dataset '{dataset}'.");
        }

        var restyled = MapEditing.Restyle(context.Arguments, layer);
        var updated = map with
        {
            Layers = map.Layers
                .Select(item => string.Equals(item.Dataset, dataset, StringComparison.Ordinal) ? restyled : item)
                .ToArray(),
        };
        return await MapEditing.CommitAsync(context, "set-style", updated, $"Would restyle layer '{dataset}' in map '{name}'.");
    }
}
