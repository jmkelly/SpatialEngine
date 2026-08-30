using System.Text.Json;
using System.Text.Json.Serialization;
using Spatial.Core.Features;

namespace Spatial.PluginSdk.Providers;

/// <summary>
/// The shared JSON interchange of dataset metadata (ADR-0028): the
/// <c>catalogue.metadata</c> items on the <c>spatial.catalogue.list@1</c>
/// stream and the single <c>dataset.description</c> item on the
/// <c>spatial.dataset.describe@1</c> stream. Metadata is small and
/// human-debuggable, so — unlike feature data, which crosses as canonical
/// binary (ADR-0020) — the plan's "JSON for debugging and public API
/// usability" rule applies to it verbatim. Field kinds use the stable
/// <see cref="AttributeKind"/> member names (wire-stable because the
/// attribute-kind enum values are the binary contract; members are the
/// human-readable names).
/// </summary>
public static class DatasetMetadataJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>Serializes one catalogue entry (a <c>catalogue.metadata</c> item).</summary>
    public static string WriteSummary(DatasetSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var document = new SummaryDocument(
            summary.Id, summary.Schema, summary.Table, summary.GeometryColumn, summary.Srid, summary.EstimatedRowCount);
        return JsonSerializer.Serialize(document, Options);
    }

    /// <summary>Deserializes a <c>catalogue.metadata</c> item.</summary>
    public static DatasetSummary ReadSummary(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var document = JsonSerializer.Deserialize<SummaryDocument>(json, Options)
            ?? throw new ArgumentException("the catalogue.metadata document is empty.", nameof(json));
        return new DatasetSummary(
            document.Id, document.Schema, document.Table, document.GeometryColumn, document.Srid, document.EstimatedRowCount);
    }

    /// <summary>Serializes a dataset description (the <c>dataset.description</c> item).</summary>
    public static string WriteDescription(DatasetDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        var document = new DescriptionDocument(
            description.Id,
            description.SchemaName,
            description.Table,
            description.GeometryColumn,
            description.Srid,
            description.GeometryType,
            description.EstimatedRowCount,
            [.. description.IdColumns],
            description.Schema.Fields
                .Select(field => new FieldDocument(field.Name, field.Kind.ToString(), field.Nullable))
                .ToArray());
        return JsonSerializer.Serialize(document, Options);
    }

    /// <summary>Deserializes a <c>dataset.description</c> item into its typed form.</summary>
    public static DatasetDescription ReadDescription(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var document = JsonSerializer.Deserialize<DescriptionDocument>(json, Options)
            ?? throw new ArgumentException("the dataset.description document is empty.", nameof(json));
        var fields = document.Fields.Select(field =>
        {
            if (!Enum.TryParse<AttributeKind>(field.Kind, ignoreCase: false, out var kind) || kind == AttributeKind.Null)
            {
                throw new ArgumentException(
                    $"the dataset.description field '{field.Name}' carries an unknown attribute kind '{field.Kind}'.", nameof(json));
            }

            return new FieldDefinition(field.Name, kind, field.Nullable);
        });
        return new DatasetDescription(
            document.Id,
            document.Schema,
            document.Table,
            document.GeometryColumn,
            document.Srid,
            document.GeometryType,
            document.EstimatedRowCount,
            document.IdColumns,
            new FeatureSchema(fields));
    }

    private sealed record SummaryDocument(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("table")] string Table,
        [property: JsonPropertyName("geometryColumn")] string GeometryColumn,
        [property: JsonPropertyName("srid")] int Srid,
        [property: JsonPropertyName("estimatedRowCount")] long EstimatedRowCount);

    private sealed record DescriptionDocument(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("table")] string Table,
        [property: JsonPropertyName("geometryColumn")] string GeometryColumn,
        [property: JsonPropertyName("srid")] int Srid,
        [property: JsonPropertyName("geometryType")] string GeometryType,
        [property: JsonPropertyName("estimatedRowCount")] long EstimatedRowCount,
        [property: JsonPropertyName("idColumns")] string[] IdColumns,
        [property: JsonPropertyName("fields")] FieldDocument[] Fields);

    private sealed record FieldDocument(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("nullable")] bool Nullable);
}
