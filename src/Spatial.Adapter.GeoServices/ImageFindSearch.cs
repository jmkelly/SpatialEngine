using Spatial.Core.Features;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Which catalog fields an ImageServer <c>find</c> request searches and
/// whether one feature hits: <c>searchFields</c> narrows the fields (unknown
/// or non-string names are dropped), otherwise every string field of the
/// catalog schema is searched, and the match is <c>contains</c> (default) or
/// <c>startsWith</c>. Split out of <see cref="ImageFindEngine"/> so the
/// search rule is named once instead of living inside the JSON writer.
/// </summary>
internal static class ImageFindSearch
{
    /// <summary>Reads the comma-separated <c>searchFields</c> parameter; absent means every string field.</summary>
    public static string[]? ParseFields(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The searched fields: the requested ones the schema carries as strings, else all of them.</summary>
    public static List<string> SearchFields(FeatureSchema schema, IReadOnlyList<string>? requested)
    {
        var fields = new List<string>();
        if (requested is null)
        {
            for (var i = 0; i < schema.Count; i++)
            {
                if (schema[i].Kind == AttributeKind.String)
                {
                    fields.Add(schema[i].Name);
                }
            }

            return fields;
        }

        foreach (var name in requested)
        {
            var index = schema.IndexOf(name);
            if (index >= 0 && schema[index].Kind == AttributeKind.String)
            {
                fields.Add(name);
            }
        }

        return fields;
    }

    /// <summary>The first field whose value hits, with the value that hit; no match is <c>null</c>.</summary>
    public static (string Field, string Value)? Match(Feature feature, IReadOnlyList<string> fields, string searchText, bool contains)
    {
        foreach (var field in fields)
        {
            if (MapFeatures.StringValue(feature, field) is not { } value)
            {
                continue;
            }

            var hit = contains
                ? value.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                : value.StartsWith(searchText, StringComparison.OrdinalIgnoreCase);
            if (hit)
            {
                return (field, value);
            }
        }

        return null;
    }
}
