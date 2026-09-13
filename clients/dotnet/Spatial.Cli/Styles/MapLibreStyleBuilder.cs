using System.Text.Json;
using System.Text.Json.Nodes;

namespace Spatial.Cli;

/// <summary>
/// Lowers the compact <see cref="DrawRecipe"/> to the persisted MapLibre
/// style fragment (ADR-0047) and infers one back (ADR-0052). The output is
/// exactly the shape <c>tools/seed/seed.mjs</c> and the workbench composer
/// produce — an array of <c>type</c>/<c>layout</c>/<c>paint</c> objects,
/// without <c>id</c> or <c>source-layer</c>, which the host injects.
/// </summary>
public static class MapLibreStyleBuilder
{
    /// <summary>Lowers a recipe and geometry family to the persisted style JSON.</summary>
    public static string Lower(DrawRecipe recipe, GeometryFamily geometry)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var visibility = recipe.Visible ? "visible" : "none";
        var layers = new JsonArray();
        if (geometry is GeometryFamily.Polygon or GeometryFamily.Mixed)
        {
            layers.Add(StyleLayer("fill", visibility, new JsonObject
            {
                ["fill-color"] = recipe.Color,
                ["fill-opacity"] = recipe.Opacity,
                ["fill-outline-color"] = recipe.Color,
            }));
        }

        if (geometry is GeometryFamily.Line or GeometryFamily.Polygon or GeometryFamily.Mixed)
        {
            layers.Add(StyleLayer("line", visibility, new JsonObject
            {
                ["line-color"] = recipe.Color,
                ["line-width"] = recipe.LineWidth,
                ["line-opacity"] = recipe.Opacity,
            }));
        }

        if (geometry is GeometryFamily.Point or GeometryFamily.Mixed)
        {
            layers.Add(StyleLayer("circle", visibility, new JsonObject
            {
                ["circle-color"] = recipe.Color,
                ["circle-radius"] = recipe.Radius,
                ["circle-opacity"] = recipe.Opacity,
                ["circle-stroke-color"] = "#0b0f14",
                ["circle-stroke-width"] = 1,
            }));
        }

        return layers.ToJsonString();
    }

    /// <summary>
    /// Infers the compact recipe from a persisted fragment. Returns
    /// <see langword="false"/> for a null/empty/malformed fragment or one
    /// with no recognisable style layer, so callers can omit the style.
    /// </summary>
    public static bool TryDescribe(string? style, out DrawRecipe recipe, out GeometryFamily family)
    {
        recipe = new DrawRecipe();
        family = GeometryFamily.Mixed;
        if (string.IsNullOrWhiteSpace(style))
        {
            return false;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(style);
        }
        catch (JsonException)
        {
            return false;
        }

        if (node is not JsonArray array || array.Count == 0)
        {
            return false;
        }

        var accumulator = new StyleAccumulator();
        foreach (var item in array)
        {
            if (item is JsonObject layer)
            {
                accumulator.Absorb(layer);
            }
        }

        if (!accumulator.HasAny)
        {
            return false;
        }

        recipe = accumulator.ToRecipe();
        family = accumulator.Family;
        return true;
    }

    private static JsonObject StyleLayer(string type, string visibility, JsonObject paint) => new()
    {
        ["type"] = type,
        ["layout"] = new JsonObject { ["visibility"] = visibility },
        ["paint"] = paint,
    };

    /// <summary>Accumulates the first recognised paint value of each kind while walking a fragment.</summary>
    private sealed class StyleAccumulator
    {
        private readonly HashSet<string> _types = new(StringComparer.Ordinal);
        private string? _color;
        private double? _opacity;
        private double? _lineWidth;
        private double? _radius;
        private bool? _visible;

        public bool HasAny => _types.Count > 0;

        public GeometryFamily Family
        {
            get
            {
                var hasFill = _types.Contains("fill");
                var hasLine = _types.Contains("line");
                var hasCircle = _types.Contains("circle");
                if (hasCircle && (hasFill || hasLine))
                {
                    return GeometryFamily.Mixed;
                }

                if (hasFill)
                {
                    return GeometryFamily.Polygon;
                }

                if (hasLine)
                {
                    return GeometryFamily.Line;
                }

                return hasCircle ? GeometryFamily.Point : GeometryFamily.Mixed;
            }
        }

        public void Absorb(JsonObject layer)
        {
            if (Text(layer["type"]) is { } type && type is "fill" or "line" or "circle")
            {
                _types.Add(type);
            }

            if (layer["layout"] is JsonObject layout && Text(layout["visibility"]) is { } visibility)
            {
                _visible ??= visibility != "none";
            }

            if (layer["paint"] is not JsonObject paint)
            {
                return;
            }

            _color ??= Text(paint["fill-color"]) ?? Text(paint["line-color"]) ?? Text(paint["circle-color"]);
            _opacity ??= Number(paint["fill-opacity"]) ?? Number(paint["line-opacity"]) ?? Number(paint["circle-opacity"]);
            _lineWidth ??= Number(paint["line-width"]);
            _radius ??= Number(paint["circle-radius"]);
        }

        public DrawRecipe ToRecipe() => new(
            _color ?? "#4fc3f7",
            _opacity ?? 1,
            _lineWidth ?? 2,
            _radius ?? 5,
            _visible ?? true);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
}
