using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;

namespace Spatial.Maps;

/// <summary>
/// Live-schema validation of a map's designated start/end date fields
/// (ADR-0183). The structural shape of a designation — identifier-shaped
/// names, a feature layer, two absent bounds meaning no designation — is pure
/// and lives in <see cref="MapValidator"/>; what only a store can answer is
/// whether the named fields exist and are date-typed at all. This type asks
/// the catalogue once per designation, so a layer whose declared extent could
/// never exist is rejected where it is declared instead of serving a
/// <c>time</c> that silently means something else.
///
/// <para>The fields are looked up in the owning layer's store, the same store
/// that holds the records they describe.</para>
/// </summary>
public static class MapTimeFieldSchemas
{
    /// <summary>Resolves one dataset's live description; a missing dataset is a <c>not.found</c> failure.</summary>
    public delegate Task<DatasetDescription> DescribeAsync(string store, string dataset, CancellationToken cancellationToken);

    /// <summary>
    /// Validates every layer's designation against the live schemas, or fails
    /// as <c>invalid.arguments</c> naming the layer and the field. Maps whose
    /// layers designate nothing cost one pass and no catalogue calls.
    /// </summary>
    public static async Task ValidateAsync(Map map, DescribeAsync describe, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(describe);
        foreach (var layer in map.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer.TimeFields is not { IsEmpty: false } designation)
            {
                continue;
            }

            var store = layer.Store ?? map.Store;
            var description = await describe(store, layer.Dataset, cancellationToken);
            foreach (var field in new[] { designation.StartField, designation.EndField })
            {
                if (field is not null)
                {
                    RequireDateField(map, layer, description, field);
                }
            }
        }
    }

    private static void RequireDateField(Map map, MapLayer layer, DatasetDescription dataset, string field)
    {
        var index = dataset.Schema.IndexOf(field);
        if (index < 0)
        {
            throw SpatialException.BadArguments(
                $"Map '{map.Name}' layer {layer.LayerId} designates date field '{field}', which dataset '{dataset.Id}' does not have.");
        }

        var kind = dataset.Schema[index].Kind;
        if (kind is not AttributeKind.DateTimeOffset)
        {
            throw SpatialException.BadArguments(
                $"Map '{map.Name}' layer {layer.LayerId} designates date field '{field}' of dataset '{dataset.Id}', whose kind is {kind}: a feature temporal extent is read from date fields (ADR-0175).");
        }
    }
}