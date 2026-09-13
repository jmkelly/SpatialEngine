using Spatial.PluginSdk.Providers;

namespace Spatial.Cli;

/// <summary>
/// Shared, pure helpers for the mutating <c>map</c> commands (ADR-0052):
/// argument parsing, append-only layer-id assignment (ADR-0041), the admin
/// token guard, dry-run planning and output shaping. Kept out of
/// <see cref="MapCommands"/> so each handler stays small.
/// </summary>
internal static class MapEditing
{
    /// <summary>The usage message shown when a mutation has no admin token.</summary>
    internal const string AdminTokenRequired = "Admin token required; pass --token or set SPATIAL_ADMIN_TOKEN.";

    /// <summary>Parses a <c>--kind</c> value case-insensitively.</summary>
    internal static PublicationKind ParseKind(string value) => value.ToLowerInvariant() switch
    {
        "feature" => PublicationKind.Feature,
        "map" => PublicationKind.Map,
        "image" => PublicationKind.Image,
        _ => throw new CliUsageException($"Unknown kind '{value}'. Use feature, map or image."),
    };

    /// <summary>
    /// Parses every repeatable <c>--layer</c> value on its first <c>=</c>,
    /// rejecting a dataset that appears more than once in one invocation.
    /// </summary>
    internal static IReadOnlyList<LayerRequest> ParseLayers(IReadOnlyList<string> specs)
    {
        var requests = new List<LayerRequest>(specs.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            var request = ParseLayer(spec);
            if (!seen.Add(request.Dataset))
            {
                throw new CliUsageException($"Duplicate layer dataset '{request.Dataset}'.");
            }

            requests.Add(request);
        }

        return requests;
    }

    /// <summary>
    /// Assigns append-only ids to the requested layers (ADR-0041): a dataset
    /// already present in <paramref name="existing"/> keeps its id, and every
    /// new dataset takes the next free id from <c>max(existing) + 1</c>.
    /// </summary>
    internal static IReadOnlyList<PublicationLayer> AssignLayers(
        IReadOnlyList<LayerRequest> requests,
        Publication? existing)
    {
        var known = new Dictionary<string, int>(StringComparer.Ordinal);
        var next = 0;
        if (existing is not null)
        {
            foreach (var layer in existing.Layers)
            {
                known[layer.Dataset] = layer.LayerId;
                next = Math.Max(next, layer.LayerId + 1);
            }
        }

        var layers = new List<PublicationLayer>(requests.Count);
        foreach (var request in requests)
        {
            if (known.TryGetValue(request.Dataset, out var id))
            {
                layers.Add(new PublicationLayer(request.Dataset, id, request.Name));
                continue;
            }

            layers.Add(new PublicationLayer(request.Dataset, next, request.Name));
            known[request.Dataset] = next;
            next++;
        }

        return layers;
    }

    /// <summary>The next free layer id for a publication: <c>max(existing) + 1</c>.</summary>
    internal static int NextLayerId(Publication publication)
    {
        var next = 0;
        foreach (var layer in publication.Layers)
        {
            next = Math.Max(next, layer.LayerId + 1);
        }

        return next;
    }

    /// <summary>The layer projecting <paramref name="dataset"/>, or <see langword="null"/>.</summary>
    internal static PublicationLayer? FindLayer(Publication publication, string dataset) =>
        publication.Layers.FirstOrDefault(layer => string.Equals(layer.Dataset, dataset, StringComparison.Ordinal));

    /// <summary>
    /// Applies the <c>set-style</c> options to <paramref name="layer"/>, starting
    /// from its current inferred recipe and overriding only the supplied fields.
    /// </summary>
    internal static PublicationLayer Restyle(CliArguments arguments, PublicationLayer layer)
    {
        var recipe = new DrawRecipe();
        var family = GeometryFamily.Mixed;
        if (MapLibreStyleBuilder.TryDescribe(layer.Style, out var current, out var currentFamily))
        {
            recipe = current;
            family = currentFamily;
        }

        if (arguments.Has("geometry"))
        {
            family = GeometryFamilies.Parse(arguments.Optional("geometry"));
        }

        if (arguments.Has("color"))
        {
            recipe = recipe with { Color = arguments.Require("color") };
        }

        if (arguments.Has("opacity"))
        {
            recipe = recipe with { Opacity = arguments.RequireDouble("opacity") };
        }

        if (arguments.Has("line-width"))
        {
            recipe = recipe with { LineWidth = arguments.RequireDouble("line-width") };
        }

        if (arguments.Has("radius"))
        {
            recipe = recipe with { Radius = arguments.RequireDouble("radius") };
        }

        if (arguments.Has("hidden"))
        {
            recipe = recipe with { Visible = false };
        }
        else if (arguments.Has("visible"))
        {
            recipe = recipe with { Visible = true };
        }

        return layer with { Style = MapLibreStyleBuilder.Lower(recipe, family) };
    }

    /// <summary>
    /// Commits a mutated publication: under <c>--dry-run</c> it prints the plan
    /// and returns success without touching the gateway; otherwise it requires
    /// the admin token and puts the publication.
    /// </summary>
    internal static async Task<int> CommitAsync(CliContext context, string action, Publication publication, string human)
    {
        if (context.Settings.DryRun)
        {
            var view = PublicationViewFactory.Describe(publication, context.Settings.Host, context.Settings.GeoServicesRoot);
            context.Output.Result(context.Command, new { action, publication = view }, human);
            return ExitCodes.Success;
        }

        var token = context.Settings.Token ?? throw new CliUsageException(AdminTokenRequired);
        var stored = await context.Gateway.PutPublicationAsync(publication, token);
        var storedView = PublicationViewFactory.Describe(stored, context.Settings.Host, context.Settings.GeoServicesRoot);
        context.Output.Result(context.Command, storedView, PublicationViewFactory.Human(storedView));
        return ExitCodes.Success;
    }

    /// <summary>Writes a <c>not.found</c> error and returns its exit code.</summary>
    internal static int NotFound(CliContext context, string message)
    {
        context.Output.Error(context.Command, "not.found", message);
        return ExitCodes.NotFound;
    }

    private static LayerRequest ParseLayer(string spec)
    {
        var separator = spec.IndexOf('=');
        var dataset = separator < 0 ? spec : spec[..separator];
        if (string.IsNullOrWhiteSpace(dataset))
        {
            throw new CliUsageException($"Invalid layer '{spec}'; expected DATASET[=NAME].");
        }

        var name = separator < 0 ? null : spec[(separator + 1)..];
        return new LayerRequest(dataset, name);
    }
}
