using System.Globalization;
using System.Text.Json;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Projects a map layer's persisted MapLibre style fragment
/// (ADR-0047, dialect ADR-0044) onto the Esri <c>drawingInfo</c>,
/// <c>labelingInfo</c> and <c>domains</c> shapes (spec §12–15, ADR-0050).
///
/// A fragment (or a set of same-kind siblings) maps to:
/// <list type="bullet">
///   <item>a <c>simple</c> renderer for one flat-colour <c>fill</c>/<c>line</c>/<c>circle</c>;</item>
///   <item>a <c>uniqueValue</c> renderer when same-kind siblings carry
///   <c>==</c>/<c>in</c> filters over one field;</item>
///   <item>a <c>classBreaks</c> renderer when same-kind siblings carry
///   <c>all</c> intervals (<c>&gt;=</c>/<c>&lt;</c>) over one numeric field;</item>
///   <item>label classes for single-field <c>symbol</c> fragments.</item>
/// </list>
/// Anything outside that subset is not projected: the layer keeps its
/// simplest symbol or no label class rather than inventing a model. Every
/// Esri type stays inside this adapter (ADR-0005); the MapLibre fragment is
/// the single style source (ADR-0047).
/// </summary>
internal static class MapStyleProjection
{
    private static readonly string[] KindPreference = ["fill", "line", "circle"];

    /// <summary>
    /// The drawing info (and label classes) for a persisted style fragment,
    /// or null when there is none. <paramref name="dataset"/> supplies the
    /// geometry family labels are placed against; it may be null for
    /// renderer-only projections.
    /// </summary>
    public static EsriDrawingInfo? Project(string? style, DatasetDescription? dataset = null)
    {
        if (!TryFragments(style, out var fragments))
        {
            return null;
        }

        foreach (var kind in KindPreference)
        {
            var siblings = fragments.Where(fragment => IsKind(fragment, kind)).ToArray();
            if (siblings.Length == 0)
            {
                continue;
            }

            var renderer = UniqueValue(siblings) ?? ClassBreaks(siblings) ?? Simple(siblings[0]);
            if (renderer is null)
            {
                continue;
            }

            var labels = dataset is null ? null : Labels(fragments, dataset);
            return new EsriDrawingInfo(renderer, labels);
        }

        return null;
    }

    /// <summary>The label classes for a persisted style fragment, or null when there are none.</summary>
    public static IReadOnlyList<EsriLabelClass>? Labels(string? style, DatasetDescription dataset) =>
        TryFragments(style, out var fragments) ? Labels(fragments, dataset) : null;

    /// <summary>
    /// Domains for the field a projected renderer classifies, validated
    /// against the catalogue schema (spec §13). The engine has no domain
    /// vocabulary of its own, so the values come from the renderer and the
    /// catalogue only vouches for the field's existence (ADR-0050).
    /// </summary>
    public static IReadOnlyDictionary<string, EsriDomain>? Domains(EsriDrawingInfo? drawingInfo, DatasetDescription dataset)
    {
        if (drawingInfo is null)
        {
            return null;
        }

        var renderer = drawingInfo.Renderer;
        var field = DomainField(renderer);
        if (string.IsNullOrWhiteSpace(field) || dataset.Schema.IndexOf(field) < 0)
        {
            return null;
        }

        var domain = BuildDomain(renderer, field);
        return domain is null
            ? null
            : new Dictionary<string, EsriDomain>(StringComparer.Ordinal) { [field] = domain };
    }

    private static string? DomainField(EsriRenderer renderer) => renderer.Type switch
    {
        "uniqueValue" => renderer.Field1,
        "classBreaks" => renderer.Field,
        _ => null,
    };

    private static EsriDomain? BuildDomain(EsriRenderer renderer, string field) => renderer switch
    {
        { Type: "uniqueValue", UniqueValueInfos: { Count: > 0 } infos } =>
            new EsriDomain(
                "codedValue",
                field,
                CodedValues: [.. infos.Select(info => new EsriCodedValue(info.Label ?? info.Value, info.Value))]),
        { Type: "classBreaks", ClassBreakInfos: { Count: > 0 } breaks, MinValue: { } min } =>
            new EsriDomain("range", field, Range: [min, breaks[^1].ClassMaxValue]),
        _ => null,
    };

    private static List<EsriLabelClass>? Labels(JsonElement[] fragments, DatasetDescription dataset)
    {
        List<EsriLabelClass>? labels = null;
        foreach (var fragment in fragments)
        {
            if (Label(fragment, dataset) is { } label)
            {
                (labels ??= []).Add(label);
            }
        }

        return labels;
    }

    private static EsriRenderer? Simple(JsonElement fragment)
    {
        var symbol = Symbol(fragment);
        return symbol is null ? null : new EsriRenderer("simple", symbol);
    }

    private static EsriRenderer? UniqueValue(JsonElement[] siblings)
    {
        var builder = new UniqueValueBuilder();
        foreach (var sibling in siblings)
        {
            if (!builder.TryAdd(sibling))
            {
                return null;
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Accumulates the categorical infos of same-kind siblings, rejecting a
    /// second field or an unreadable symbol rather than inventing a model.
    /// </summary>
    private sealed class UniqueValueBuilder
    {
        private readonly List<EsriUniqueValueInfo> _infos = [];
        private string? _field;
        private EsriSymbol? _defaultSymbol;

        public bool TryAdd(JsonElement sibling)
        {
            if (Filter(sibling) is not { } expression)
            {
                _defaultSymbol ??= Symbol(sibling);
                return true;
            }

            if (!TryCategorical(expression, out var categorical))
            {
                return false;
            }

            if (_field is not null && !string.Equals(_field, categorical.Field, StringComparison.Ordinal))
            {
                return false;
            }

            if (Symbol(sibling) is not { } symbol)
            {
                return false;
            }

            _field ??= categorical.Field;
            foreach (var value in categorical.Values)
            {
                _infos.Add(new EsriUniqueValueInfo(value, symbol));
            }

            return true;
        }

        public EsriRenderer? Build() =>
            _field is not null && _infos.Count > 0
                ? new EsriRenderer(
                    "uniqueValue",
                    Field1: _field,
                    DefaultSymbol: _defaultSymbol,
                    DefaultLabel: _defaultSymbol is null ? null : "<Other values>",
                    UniqueValueInfos: _infos)
                : null;
    }

    private static EsriRenderer? ClassBreaks(JsonElement[] siblings)
    {
        if (siblings.Length < 2)
        {
            return null;
        }

        var builder = new ClassBreaksBuilder();
        foreach (var sibling in siblings)
        {
            if (!builder.TryAdd(sibling))
            {
                return null;
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Accumulates the interval infos of same-kind siblings, rejecting a
    /// second field or an unreadable symbol rather than inventing a model.
    /// </summary>
    private sealed class ClassBreaksBuilder
    {
        private readonly List<(double Max, EsriSymbol Symbol)> _classes = [];
        private string? _field;
        private double _minValue = double.PositiveInfinity;

        public bool TryAdd(JsonElement sibling)
        {
            if (Filter(sibling) is not { } expression || !TryInterval(expression, out var interval))
            {
                return false;
            }

            if (_field is not null && !string.Equals(_field, interval.Field, StringComparison.Ordinal))
            {
                return false;
            }

            if (Symbol(sibling) is not { } symbol)
            {
                return false;
            }

            _field ??= interval.Field;
            _minValue = Math.Min(_minValue, interval.Lower);
            _classes.Add((interval.Upper, symbol));
            return true;
        }

        public EsriRenderer? Build() =>
            _field is not null && _classes.Count > 0
                ? new EsriRenderer(
                    "classBreaks",
                    Field: _field,
                    MinValue: _minValue,
                    ClassBreakInfos: [.. _classes
                        .OrderBy(entry => entry.Max)
                        .Select(entry => new EsriClassBreakInfo(entry.Max, entry.Symbol, Format(entry.Max)))])
                : null;
    }

    private static EsriLabelClass? Label(JsonElement fragment, DatasetDescription dataset)
    {
        if (!IsKind(fragment, "symbol")
            || !fragment.TryGetProperty("layout", out var layout)
            || layout.ValueKind != JsonValueKind.Object
            || !layout.TryGetProperty("text-field", out var expression)
            || !TryField(expression, out var field))
        {
            return null;
        }

        var paint = Paint(fragment);
        var anchor = StringValue(layout, "text-anchor", "center");
        return new EsriLabelClass(
            Placement(dataset.GeometryType, anchor),
            $"[{field}]",
            UseCodedValues: false,
            new EsriTextSymbol(
                "esriTS",
                EsriColor.ToRgba(StringValue(paint, "text-color", "#000000"), 1.0),
                BackgroundColor: null,
                BorderLineColor: null,
                VerticalAlignment(anchor),
                HorizontalAlignment(anchor),
                RightToLeft: false,
                Angle: 0,
                XOffset: 0,
                YOffset: 0,
                new EsriFont(FontFamily(layout), Number(layout, "text-size", 12), "normal", "normal", "none")),
            MinScale: 0,
            MaxScale: 0);
    }

    private static EsriSymbol? Symbol(JsonElement fragment) => IsSymbolKind(fragment)
        ? Symbol(fragment.GetProperty("type").GetString()!, Paint(fragment))
        : null;

    private static EsriSymbol? Symbol(string kind, JsonElement paint) => kind switch
    {
        "fill" => Fill(paint),
        "line" => Line(paint),
        "circle" => Circle(paint),
        _ => null,
    };

    private static EsriSymbol Fill(JsonElement paint)
    {
        var opacity = Number(paint, "fill-opacity", 1.0);
        var fill = Color(paint, "fill-color", "#000000", opacity);
        var outlineWidth = Number(paint, "fill-outline-width", 0);
        var outlineColor = Color(paint, "fill-outline-color", null, opacity);
        var outline = outlineWidth > 0 && outlineColor is { } stroke
            ? new EsriSymbolOutline("esriSLS", "esriSLSSolid", stroke, outlineWidth)
            : null;
        return new EsriSymbol("esriSFS", "esriSFSSolid", fill, Outline: outline);
    }

    private static EsriSymbol Line(JsonElement paint)
    {
        var opacity = Number(paint, "line-opacity", 1.0);
        var color = Color(paint, "line-color", "#000000", opacity);
        var width = Number(paint, "line-width", 1.0);
        return new EsriSymbol("esriSLS", "esriSLSSolid", color, Width: width);
    }

    private static EsriSymbol Circle(JsonElement paint)
    {
        var opacity = Number(paint, "circle-opacity", 1.0);
        var color = Color(paint, "circle-color", "#000000", opacity);
        var radius = Number(paint, "circle-radius", 5.0);
        var strokeWidth = Number(paint, "circle-stroke-width", 0);
        var strokeColor = Color(paint, "circle-stroke-color", null, opacity);
        var outline = strokeWidth > 0 && strokeColor is { } stroke
            ? new EsriSymbolOutline("esriSLS", "esriSLSSolid", stroke, strokeWidth)
            : null;
        return new EsriSymbol("esriSMS", "esriSMSCircle", color, Size: radius * 2, Outline: outline);
    }

    /// <summary>Whether the fragment is an object of one of the drawable symbol kinds.</summary>
    private static bool IsSymbolKind(JsonElement fragment) =>
        KindPreference.Any(kind => IsKind(fragment, kind));

    private static bool IsKind(JsonElement fragment, string kind) =>
        fragment.ValueKind == JsonValueKind.Object
        && fragment.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && string.Equals(type.GetString(), kind, StringComparison.Ordinal);

    private static JsonElement Paint(JsonElement fragment) =>
        fragment.ValueKind == JsonValueKind.Object
        && fragment.TryGetProperty("paint", out var paint)
        && paint.ValueKind == JsonValueKind.Object
            ? paint
            : default;

    private static JsonElement? Filter(JsonElement fragment) =>
        fragment.ValueKind == JsonValueKind.Object
        && fragment.TryGetProperty("filter", out var filter)
        && filter.ValueKind == JsonValueKind.Array
        && filter.GetArrayLength() > 0
            ? filter
            : null;

    /// <summary>Reads <c>["==", field, value]</c> or <c>["in", field, v…]</c>.</summary>
    private static bool TryCategorical(JsonElement filter, out (string Field, IReadOnlyList<string> Values) categorical)
    {
        categorical = default;
        if (filter[0].ValueKind != JsonValueKind.String || filter[1].ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var field = filter[1].GetString()!;
        if (filter[0].GetString() == "==" && filter.GetArrayLength() == 3)
        {
            categorical = (field, [Format(filter[2])]);
            return true;
        }

        if (filter[0].GetString() == "in" && filter.GetArrayLength() >= 3)
        {
            var values = new List<string>(filter.GetArrayLength() - 2);
            for (var i = 2; i < filter.GetArrayLength(); i++)
            {
                values.Add(Format(filter[i]));
            }

            categorical = (field, values);
            return true;
        }

        return false;
    }

    /// <summary>Reads <c>["all", [">=", field, lo], ["&lt;", field, hi]]</c>.</summary>
    private static bool TryInterval(JsonElement filter, out (string Field, double Lower, double Upper) interval)
    {
        interval = default;
        if (filter[0].ValueKind != JsonValueKind.String
            || filter[0].GetString() != "all"
            || filter.GetArrayLength() != 3
            || !Comparison(filter[1], ">=", out var lowerField, out var lower)
            || !Comparison(filter[2], "<", out var upperField, out var upper)
            || !string.Equals(lowerField, upperField, StringComparison.Ordinal))
        {
            return false;
        }

        interval = (lowerField, lower, upper);
        return true;
    }

    private static bool Comparison(JsonElement filter, string op, out string field, out double value)
    {
        field = string.Empty;
        value = 0;
        if (filter.ValueKind != JsonValueKind.Array
            || filter.GetArrayLength() != 3
            || filter[0].ValueKind != JsonValueKind.String
            || filter[0].GetString() != op
            || filter[1].ValueKind != JsonValueKind.String
            || filter[2].ValueKind != JsonValueKind.Number
            || !filter[2].TryGetDouble(out value))
        {
            return false;
        }

        field = filter[1].GetString()!;
        return true;
    }

    /// <summary>Whether a <c>text-field</c> names exactly one field.</summary>
    private static bool TryField(JsonElement expression, out string field)
    {
        field = expression.ValueKind switch
        {
            JsonValueKind.String => BracedField(expression.GetString()),
            JsonValueKind.Array => GetExpressionField(expression),
            _ => string.Empty,
        };
        return field.Length > 0;
    }

    private static string BracedField(string? text) =>
        text is { Length: > 2 } && text[0] == '{' && text[^1] == '}'
            ? text[1..^1]
            : string.Empty;

    private static string GetExpressionField(JsonElement expression) =>
        expression.GetArrayLength() == 2
        && expression[0].ValueKind == JsonValueKind.String
        && expression[0].GetString() == "get"
        && expression[1].ValueKind == JsonValueKind.String
            ? expression[1].GetString() ?? string.Empty
            : string.Empty;

    private static readonly Dictionary<string, string> AnchorSuffixes = new(StringComparer.Ordinal)
    {
        ["top"] = "AboveCenter",
        ["bottom"] = "BelowCenter",
        ["left"] = "CenterLeft",
        ["right"] = "CenterRight",
        ["top-left"] = "AboveLeft",
        ["top-right"] = "AboveRight",
        ["bottom-left"] = "BelowLeft",
        ["bottom-right"] = "BelowRight",
    };

    private static string Placement(string geometry, string anchor)
    {
        var family = geometry.Trim().ToLowerInvariant();
        if (IsLine(family))
        {
            return "esriServerLinePlacementCenterAlong";
        }

        if (IsPolygon(family))
        {
            return "esriServerPolygonPlacementAlwaysHorizontal";
        }

        return "esriServerPointLabelPlacement" + AnchorSuffix(anchor);
    }

    private static bool IsLine(string family) =>
        family.StartsWith("line", StringComparison.Ordinal) || family.StartsWith("multiline", StringComparison.Ordinal);

    private static bool IsPolygon(string family) =>
        family.StartsWith("polygon", StringComparison.Ordinal) || family.StartsWith("multipolygon", StringComparison.Ordinal);

    private static string AnchorSuffix(string anchor) =>
        AnchorSuffixes.TryGetValue(anchor, out var suffix) ? suffix : "CenterCenter";

    private static string VerticalAlignment(string anchor) => anchor switch
    {
        "top" or "top-left" or "top-right" => "top",
        "bottom" or "bottom-left" or "bottom-right" => "bottom",
        _ => "middle",
    };

    private static string HorizontalAlignment(string anchor) => anchor switch
    {
        "left" or "top-left" or "bottom-left" => "left",
        "right" or "top-right" or "bottom-right" => "right",
        _ => "center",
    };

    private static string FontFamily(JsonElement layout) =>
        layout.ValueKind == JsonValueKind.Object && layout.TryGetProperty("text-font", out var font)
            ? FontName(font)
            : "Arial";

    private static string FontName(JsonElement font) => font.ValueKind switch
    {
        JsonValueKind.String => font.GetString() ?? "Arial",
        JsonValueKind.Array => FirstFont(font),
        _ => "Arial",
    };

    private static string FirstFont(JsonElement font) =>
        font.GetArrayLength() > 0 && font[0].ValueKind == JsonValueKind.String
            ? font[0].GetString() ?? "Arial"
            : "Arial";

    private static string StringValue(JsonElement element, string name, string fallback) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static int[] Color(JsonElement paint, string name, string? fallback, double opacity)
    {
        var css = paint.ValueKind == JsonValueKind.Object
            && paint.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : fallback;
        return EsriColor.ToRgba(css, opacity);
    }

    private static double Number(JsonElement element, string name, double fallback) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : fallback;

    private static string Format(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.TryGetInt64(out var integer) ? integer.ToString(CultureInfo.InvariantCulture) : value.GetDouble().ToString(CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses the fragment array, cloning the elements so the document can be disposed.</summary>
    private static bool TryFragments(string? style, out JsonElement[] fragments)
    {
        fragments = [];
        if (string.IsNullOrWhiteSpace(style))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(style);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            fragments = [.. document.RootElement.EnumerateArray().Select(element => element.Clone())];
            return fragments.Length > 0;
        }
    }
}

/// <summary>
/// The CSS-ish colour subset the persisted style uses (hex, <c>rgb()</c>/
/// <c>rgba()</c> and a few names) projected to an Esri RGBA quad. MapLibre
/// accepts more syntaxes; the composer emits hex, and an unparsable value
/// falls back to transparent rather than failing the whole layer.
/// </summary>
internal static class EsriColor
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "#000000",
        ["white"] = "#ffffff",
        ["red"] = "#ff0000",
        ["green"] = "#008000",
        ["blue"] = "#0000ff",
        ["yellow"] = "#ffff00",
        ["gray"] = "#808080",
        ["grey"] = "#808080",
        ["transparent"] = "#00000000",
    };

    /// <summary>Parses a colour and applies an extra opacity (0–1) to its alpha.</summary>
    public static int[] ToRgba(string? css, double opacity)
    {
        var rgba = Parse(css) ?? [0, 0, 0, 0];
        rgba[3] = (int)Math.Round(Math.Clamp(rgba[3] / 255.0 * opacity, 0, 1) * 255);
        return rgba;
    }

    private static int[]? Parse(string? css)
    {
        if (string.IsNullOrWhiteSpace(css))
        {
            return null;
        }

        var text = css.Trim();
        if (Names.TryGetValue(text, out var named))
        {
            text = named;
        }

        if (text.StartsWith('#'))
        {
            return ParseHex(text[1..]);
        }

        if (text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            return ParseFunctional(text);
        }

        return null;
    }

    private static int[]? ParseHex(string hex) => hex.Length switch
    {
        3 or 4 => ParseShortHex(hex),
        6 or 8 => ParseLongHex(hex),
        _ => null,
    };

    private static int[] ParseShortHex(string hex)
    {
        var channels = new int[4];
        for (var index = 0; index < hex.Length; index++)
        {
            channels[index] = Channel(hex[index]);
        }

        if (hex.Length == 3)
        {
            channels[3] = 255;
        }

        return channels;
    }

    private static int[] ParseLongHex(string hex)
    {
        var channels = new int[4];
        for (var index = 0; index < hex.Length / 2; index++)
        {
            channels[index] = Byte(hex, index * 2);
        }

        if (hex.Length == 6)
        {
            channels[3] = 255;
        }

        return channels;
    }

    private static int[]? ParseFunctional(string text)
    {
        var open = text.IndexOf('(');
        var close = text.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return null;
        }

        var parts = text[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length is 3 or 4 ? ParseChannels(parts) : null;
    }

    private static int[]? ParseChannels(string[] parts)
    {
        var channels = new int[4];
        for (var index = 0; index < 3; index++)
        {
            if (!TryChannel(parts[index], out channels[index]))
            {
                return null;
            }
        }

        channels[3] = parts.Length == 4 && TryAlpha(parts[3], out var alpha) ? alpha : 255;
        return channels;
    }

    private static bool TryChannel(string part, out int channel)
    {
        channel = 0;
        if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        channel = (int)Math.Clamp(Math.Round(value), 0, 255);
        return true;
    }

    private static bool TryAlpha(string part, out int alpha)
    {
        alpha = 0;
        if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        alpha = (int)Math.Clamp(Math.Round(value * 255), 0, 255);
        return true;
    }

    private static int Byte(string hex, int index) =>
        int.TryParse(hex.AsSpan(index, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static int Channel(char value) => Byte(new string(value, 2), 0);
}
