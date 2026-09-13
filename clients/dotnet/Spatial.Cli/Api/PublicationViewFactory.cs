using System.Text;
using Spatial.PluginSdk.Providers;

namespace Spatial.Cli;

/// <summary>A publication rendered for a client: metadata, GeoServices endpoint and recognised layer styles.</summary>
public sealed record PublicationView(
    string Name,
    string Kind,
    string Store,
    string? Description,
    string? Copyright,
    string? Endpoint,
    IReadOnlyList<PublicationLayerView> Layers);

/// <summary>One layer of a <see cref="PublicationView"/> with its inferred compact style.</summary>
public sealed record PublicationLayerView(
    int LayerId,
    string Dataset,
    string? Name,
    string? Geometry,
    DrawRecipe? Style);

/// <summary>
/// Turns a core <see cref="Publication"/> into the CLI's view (ADR-0052):
/// the GeoServices endpoint it projects to and each layer's compact draw
/// recipe, inferred from the persisted MapLibre fragment (ADR-0047).
/// </summary>
public static class PublicationViewFactory
{
    /// <summary>Describes a publication for <c>map show</c> and <c>map export</c>.</summary>
    public static PublicationView Describe(Publication publication, string host, string geoServicesRoot)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return new PublicationView(
            publication.Name,
            KindName(publication.Kind),
            publication.Store,
            publication.Description,
            publication.Copyright,
            PublicationEndpoints.For(host, geoServicesRoot, publication),
            publication.Layers.Select(DescribeLayer).ToArray());
    }

    /// <summary>Renders a publication view as human-readable text.</summary>
    public static string Human(PublicationView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var builder = new StringBuilder();
        builder.Append(view.Name).Append(" — ").Append(view.Kind).Append(" (").Append(view.Store).Append(')');
        if (view.Endpoint is not null)
        {
            builder.Append(Environment.NewLine).Append("  endpoint: ").Append(view.Endpoint);
        }

        foreach (var layer in view.Layers)
        {
            builder.Append(Environment.NewLine)
                .Append("  ").Append(layer.LayerId).Append(": ").Append(layer.Dataset);
            if (layer.Name is not null)
            {
                builder.Append(" (").Append(layer.Name).Append(')');
            }

            if (layer.Geometry is not null)
            {
                builder.Append(" [").Append(layer.Geometry).Append(']');
            }
        }

        return builder.ToString();
    }

    private static PublicationLayerView DescribeLayer(PublicationLayer layer)
    {
        var hasStyle = MapLibreStyleBuilder.TryDescribe(layer.Style, out var recipe, out var family);
        return new PublicationLayerView(
            layer.LayerId,
            layer.Dataset,
            layer.Name,
            hasStyle ? GeometryFamilies.Name(family) : null,
            hasStyle ? recipe : null);
    }

    private static string KindName(PublicationKind kind) => kind.ToString().ToLowerInvariant();
}
