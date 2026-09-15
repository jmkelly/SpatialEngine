using System.Text.RegularExpressions;
using Spatial.Core.Features;
using Spatial.PluginSdk.Providers;

namespace Spatial.Stores.Memory;

/// <summary>
/// One in-memory spatial dataset (ADR-0042): identity, the stored feature
/// schema (identity column included), the columns forming the feature
/// identity and the current features. Mutable only through
/// <see cref="MemoryCatalogue"/> under its single lock, so a caller never sees
/// a half-applied edit.
/// </summary>
internal sealed class MemoryDataset
{
    private static readonly Regex Pattern = new(@"^[a-z_][a-z0-9_]*\.[a-z_][a-z0-9_]*$", RegexOptions.Compiled);

    private MemoryDataset(
        string id, string schemaName, string table, string geometryColumn, int srid,
        FeatureSchema schema, IReadOnlyList<string> idColumns, List<Feature> features, long nextId)
    {
        Id = id;
        SchemaName = schemaName;
        Table = table;
        GeometryColumn = geometryColumn;
        Srid = srid;
        Schema = schema;
        IdColumns = idColumns;
        Features = features;
        NextId = nextId;
    }

    public string Id { get; }

    public string SchemaName { get; }

    public string Table { get; }

    public string GeometryColumn { get; }

    public int Srid { get; }

    public FeatureSchema Schema { get; }

    public IReadOnlyList<string> IdColumns { get; }

    public List<Feature> Features { get; }

    public long NextId { get; set; }

    /// <summary>Whether the dataset can be edited: it has at least one identity column.</summary>
    public bool Editable => IdColumns.Count > 0;

    public static bool IsValidId(string? id) => id is not null && Pattern.IsMatch(id);

    /// <summary>Builds a dataset from a validated identifier and a stored schema.</summary>
    public static MemoryDataset Create(
        string id, int srid, FeatureSchema storedSchema, IReadOnlyList<string> idColumns, List<Feature> features, long nextId)
    {
        var (schemaName, table) = Split(id);
        FieldDefinition? geometry = null;
        foreach (var field in storedSchema.Fields)
        {
            if (field.Kind == AttributeKind.Geometry)
            {
                geometry = field;
                break;
            }
        }

        if (geometry is not { } shape)
        {
            throw new ArgumentException($"Dataset '{id}' has no geometry field.", nameof(storedSchema));
        }

        return new MemoryDataset(id, schemaName, table, shape.Name, srid, storedSchema, idColumns, features, nextId);
    }

    public DatasetSummary ToSummary() => new(Id, SchemaName, Table, GeometryColumn, Srid, Features.Count);

    public DatasetDescription ToDescription() =>
        new(Id, SchemaName, Table, GeometryColumn, Srid, GeometryTypeName(), Features.Count, IdColumns, Schema);

    /// <summary>
    /// The dataset's geometry type inferred from the first non-null geometry
    /// value, so a GeoServices layer advertises the real Esri geometry rather
    /// than a placeholder. An empty or all-null dataset falls back to
    /// <c>Geometry</c>.
    /// </summary>
    private string GeometryTypeName()
    {
        foreach (var feature in Features)
        {
            foreach (var attribute in feature.Attributes)
            {
                if (attribute.Kind == AttributeKind.Geometry && !attribute.IsNull)
                {
                    return attribute.GeometryValue.Type.ToString();
                }
            }
        }

        return "Geometry";
    }

    /// <summary>Copies the dataset with a fresh feature list (used for transaction snapshots).</summary>
    public MemoryDataset Clone() =>
        new(Id, SchemaName, Table, GeometryColumn, Srid, Schema, IdColumns, Features.ToList(), NextId);

    private static (string SchemaName, string Table) Split(string id)
    {
        var dot = id.IndexOf('.');
        return (id[..dot], id[(dot + 1)..]);
    }
}
