using System.Globalization;
using System.Text;
using Spatial.Core.Features;

namespace Spatial.Rendering.Skia.Pipeline;

/// <summary>
/// Expands the MapLibre <c>text-field</c>/<c>icon-image</c> templates of a
/// symbol layer from feature attributes (ADR-0049). Extracted from
/// <see cref="SymbolSceneBuilder"/> so template parsing and the attribute
/// value switch live in one place.
/// </summary>
internal static class SymbolTemplateResolver
{
    /// <summary>
    /// Expands a MapLibre <c>text-field</c>/<c>icon-image</c> template:
    /// <c>{attribute}</c> tokens are replaced by attribute text. Returns
    /// <c>null</c> when a token names a missing attribute, which skips that
    /// part of the symbol (never a half-rendered label).
    /// </summary>
    public static string? Resolve(string template, IFeature feature)
    {
        if (template.Length == 0)
        {
            return null;
        }

        var builder = new StringBuilder(template.Length);
        var index = 0;
        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            builder.Append(template, index, open - index);
            var close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                builder.Append(template, open, template.Length - open);
                break;
            }

            if (ReadAttribute(feature, template[(open + 1)..close]) is not { } text)
            {
                return null;
            }

            builder.Append(text);
            index = close + 1;
        }

        return builder.ToString();
    }

    public static string? ReadAttribute(IFeature feature, string name)
    {
        var index = feature.Schema.IndexOf(name);
        if (index < 0)
        {
            return null;
        }

        var value = feature[index];
        return value.Kind switch
        {
            AttributeKind.String => value.StringValue,
            AttributeKind.Int64 => value.Int64Value.ToString(CultureInfo.InvariantCulture),
            AttributeKind.Double => value.DoubleValue.ToString(CultureInfo.InvariantCulture),
            AttributeKind.Boolean => value.BooleanValue ? "true" : "false",
            AttributeKind.Guid => value.GuidValue.ToString(),
            AttributeKind.DateTimeOffset => value.DateTimeOffsetValue.ToString("O", CultureInfo.InvariantCulture),
            _ => null,
        };
    }
}
