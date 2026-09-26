using System.Collections.Frozen;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer legend resources (spec §4, ADR-0055): <c>legend</c>,
/// <c>queryDomains</c> and <c>queryLegends</c> over the selected layers, plus
/// the <c>layers</c> selection grammar they share — a bare id list or the
/// show/hide/all form, with unknown ids typed <c>not.found</c>. Split out of
/// <see cref="MapServerEndpoints"/>, which owns the routes.
/// </summary>
internal static class MapServerLegendEndpoints
{
    /// <summary>
    /// The MapServer <c>legend</c> resource (S4 legend-map-service/, ADR-0055):
    /// one swatch legend per layer, projected from the persisted style so it
    /// always agrees with the layer metadata <c>drawingInfo</c>.
    /// </summary>
    public static async Task<IResult> MapLegend(MapServerRequest request)
    {
        try
        {
            var scope = await MapServerScope.OpenAsync(request);
            return EsriJson.Value(Adapter.GeoServices.MapLegend.Legend(await scope.InfosAsync()));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The MapServer <c>queryDomains</c> operation (S4, ADR-0055): the
    /// projected domains of the selected layers (all layers when
    /// <c>layers</c> is absent). Unknown layer ids are typed
    /// <c>not.found</c>.
    /// </summary>
    public static async Task<IResult> MapQueryDomains(MapServerRequest request)
    {
        try
        {
            var scope = await MapServerScope.OpenAsync(request);
            var infos = await scope.InfosAsync();
            return EsriJson.Value(Adapter.GeoServices.MapLegend.QueryDomains(
                Select(infos, scope.Parameter("layers"), request.Service)));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The MapServer <c>queryLegends</c> operation (S4, ADR-0055): the legend
    /// of the selected layers (all layers when <c>layers</c> is absent).
    /// Unknown layer ids are typed <c>not.found</c>.
    /// </summary>
    public static async Task<IResult> MapQueryLegends(MapServerRequest request)
    {
        try
        {
            var scope = await MapServerScope.OpenAsync(request);
            var infos = await scope.InfosAsync();
            return EsriJson.Value(Adapter.GeoServices.MapLegend.QueryLegends(
                Select(infos, scope.Parameter("layers"), request.Service)));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// Selects legend/domain layers by the <c>layers</c> parameter (a bare id
    /// list, or the shared show/hide/all grammar); unknown ids are typed
    /// <c>not.found</c> rather than silently dropped.
    /// </summary>
    private static IReadOnlyList<MapLayerInfo> Select(IReadOnlyList<MapLayerInfo> infos, string? selection, string service)
    {
        var selected = MapLayerSelection.Select(infos, selection);
        if (selection is not null)
        {
            RejectUnknownLayers(infos, selection, service, selected);
        }

        return selected;
    }

    /// <summary>
    /// An explicit selection that resolved to fewer layers than the service holds
    /// means the grammar named an id the service does not have; those are typed
    /// <c>not.found</c> rather than silently dropped.
    /// </summary>
    private static void RejectUnknownLayers(
        IReadOnlyList<MapLayerInfo> infos,
        string selection,
        string service,
        IReadOnlyList<MapLayerInfo> selected)
    {
        if (selected.Count == infos.Count)
        {
            return;
        }

        var known = infos.Select(info => info.Layer.Id).ToHashSet();
        var unknown = RequestedIds(selection).Except(known).ToList();
        if (unknown.Count != 0)
        {
            throw GeoServicesErrors.NotFound($"Layer {unknown[0]} does not exist in service '{service}'.");
        }
    }

    public static IReadOnlyList<int> RequestedIds(string selection) => ParseSelection(StripShowPrefix(selection.Trim()));

    /// <summary>No selection (a <c>hide:</c> form) or an "all layers" keyword selects no ids at all.</summary>
    private static IReadOnlyList<int> ParseSelection(string? text) =>
        SelectsLayers(text) ? TryParseIdList(text!) : [];

    private static bool SelectsLayers(string? text) => text is not null && !IsSelectAllKeyword(text);

    private static readonly string ShowPrefix = "show:";
    private static readonly string HidePrefix = "hide:";

    /// <summary>Strips a <c>show:</c> prefix; a <c>hide:</c> selection resolves to no layers at all.</summary>
    private static string? StripShowPrefix(string text)
    {
        var shown = text[PrefixLength(text)..];
        return text.StartsWith(HidePrefix, StringComparison.OrdinalIgnoreCase) ? null : shown;
    }

    private static int PrefixLength(string text) =>
        text.StartsWith(ShowPrefix, StringComparison.OrdinalIgnoreCase) ? ShowPrefix.Length : 0;

    /// <summary>The Esri keywords that mean "every layer" (spec §4, <c>show:</c> selection).</summary>
    private static readonly FrozenSet<string> SelectAllKeywords =
        new[] { "all", "visible", "top" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsSelectAllKeyword(string text) =>
        string.IsNullOrWhiteSpace(text) || SelectAllKeywords.Contains(text);

    private static IReadOnlyList<int> TryParseIdList(string text)
    {
        try
        {
            return [.. EsriValueParser.ParseInt64s(text, "layers").Select(value => (int)value)];
        }
        catch (EsriInteropException)
        {
            return [];
        }
    }
}
