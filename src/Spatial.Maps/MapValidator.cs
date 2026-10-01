using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;

namespace Spatial.Maps;

/// <summary>
/// Pure validation and normalisation of a <see cref="Map"/> (ADR-0053 §2).
/// Map names are flat and identifier-shaped (folder syntax is rejected); a
/// feature layer's dataset uses the strict <c>schema.table</c> grammar, an
/// image layer names a raster dataset; layer ids are unique and non-negative.
/// A negative layer id means "assign the next free id", which is how the host
/// and the admin projections create maps without hard-coding ids.
///
/// <para>Every enabled service must be fed: Feature/Map/Tiles/WMS/WFS need at
/// least one feature layer and Image needs at least one image layer, so a map
/// never advertises a surface it cannot serve.</para>
/// </summary>
internal static class MapValidator
{
    private static readonly Regex NamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex DatasetPattern = new(@"^[a-z_][a-z0-9_]*\.[a-z_][a-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex RasterDatasetPattern = new(@"^[A-Za-z_][A-Za-z0-9_.\-]*$", RegexOptions.Compiled);
    private static readonly Regex ColumnPattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>Validates and normalises a map, assigning any negative layer ids.</summary>
    public static Map Normalize(Map map, int nextLayerId)
    {
        ArgumentNullException.ThrowIfNull(map);
        ValidateHeader(map);
        return map with { Layers = NormalizeLayers(map, nextLayerId) };
    }

    /// <summary>
    /// A relationship column is an ordinary attribute name: identifier-shaped
    /// and nothing else, so a declaration can never smuggle SQL structure
    /// into the closed where-grammar the traversal builds from it.
    /// </summary>
    internal static bool IsValidColumn(string? column) => column is not null && ColumnPattern.IsMatch(column);

    private static void ValidateHeader(Map map)
    {
        if (!NamePattern.IsMatch(map.Name ?? string.Empty))
        {
            throw SpatialException.BadArguments(
                $"Map name '{map.Name}' is invalid: expected a flat identifier [A-Za-z_][A-Za-z0-9_]* (folders are not supported).");
        }

        if (string.IsNullOrWhiteSpace(map.Store))
        {
            throw SpatialException.BadArguments($"Map '{map.Name}' needs a non-empty store.");
        }

        if (map.Layers.Count == 0)
        {
            throw SpatialException.BadArguments($"Map '{map.Name}' must expose at least one layer.");
        }

        ValidateServices(map);
        ValidateMetadata(map);
    }

    /// <summary>
    /// The authored service metadata must be well-formed XML when present
    /// (ADR-0068): the ImageServer serves these bytes verbatim, so a broken
    /// document fails here with <c>invalid.arguments</c>, never at request time.
    /// Whole-store declared maps skip <see cref="Normalize"/>, so
    /// <see cref="MapRegistry"/> calls this directly for those too.
    /// </summary>
    internal static void ValidateMetadata(Map map)
    {
        if (string.IsNullOrWhiteSpace(map.MetadataXml))
        {
            return;
        }

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(map.MetadataXml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            while (reader.Read())
            {
            }
        }
        catch (XmlException exception)
        {
            throw SpatialException.BadArguments(
                $"Map '{map.Name}' has service metadata that is not well-formed XML: {exception.Message}");
        }
    }

    private static void ValidateServices(Map map)
    {
        var seen = new HashSet<MapServiceKind>();
        foreach (var service in map.Services)
        {
            if (!Enum.IsDefined(service))
            {
                throw SpatialException.BadArguments($"Map '{map.Name}' has unknown service '{service}'.");
            }

            if (!seen.Add(service))
            {
                throw SpatialException.BadArguments($"Map '{map.Name}' lists service '{service}' more than once.");
            }
        }
    }

    private static List<MapLayer> NormalizeLayers(Map map, int nextLayerId)
    {
        var layers = new List<MapLayer>(map.Layers.Count);
        var ids = new HashSet<int>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var next = nextLayerId;
        var hasFeature = false;
        var hasImage = false;
        foreach (var layer in map.Layers)
        {
            ValidateLayer(map, layer, keys);
            hasFeature |= layer.Kind == MapLayerKind.Feature;
            hasImage |= layer.Kind == MapLayerKind.Image;
            layers.Add(layer with { LayerId = AssignId(map, layer, ids, ref next) });
        }

        RequireLayers(map, hasFeature, hasImage);
        ValidateRelationships(map, layers);
        return layers;
    }

    private static void RequireLayers(Map map, bool hasFeature, bool hasImage)
    {
        var needsFeature = map.Services.Any(service => service is MapServiceKind.FeatureServer or MapServiceKind.MapServer or MapServiceKind.Tiles or MapServiceKind.Wms or MapServiceKind.Wfs);
        if (needsFeature && !hasFeature)
        {
            throw SpatialException.BadArguments(
                $"Map '{map.Name}' enables a vector service but has no feature layer.");
        }

        if (map.Exposes(MapServiceKind.ImageServer) && !hasImage)
        {
            throw SpatialException.BadArguments(
                $"Map '{map.Name}' enables the image service but has no image layer.");
        }
    }

    /// <summary>
    /// Structural relationship validation (ADR-0077): every relationship
    /// names a layer of this map, carries identifier-shaped column names and
    /// a name unique within its layer, and pairs the many-to-many
    /// cardinality with a join dataset and every other cardinality with
    /// none. The live schemas the columns must exist in are checked where a
    /// declaration happens, against the catalogue (the stores are
    /// providers, so the registry never opens one).
    /// </summary>
    private static void ValidateRelationships(Map map, IReadOnlyList<MapLayer> layers)
    {
        var byId = layers.ToDictionary(layer => layer.LayerId);
        foreach (var layer in layers)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relationship in layer.Relationships ?? [])
            {
                if (!NamePattern.IsMatch(relationship.Name ?? string.Empty))
                {
                    throw SpatialException.BadArguments(
                        $"Map '{map.Name}' has a relationship on layer {layer.LayerId} named '{relationship.Name}': expected a flat identifier [A-Za-z_][A-Za-z0-9_]*.");
                }

                if (!names.Add(relationship.Name!))
                {
                    throw SpatialException.BadArguments(
                        $"Map '{map.Name}' declares relationship '{relationship.Name}' more than once on layer {layer.LayerId}.");
                }

                if (layer.Kind != MapLayerKind.Feature)
                {
                    throw SpatialException.BadArguments(
                        $"Map '{map.Name}' relationship '{relationship.Name}' is declared on layer {layer.LayerId}, which is not a feature layer.");
                }

                if (!byId.TryGetValue(relationship.RelatedLayerId, out _))
                {
                    throw SpatialException.BadArguments(
                        $"Map '{map.Name}' relationship '{relationship.Name}' on layer {layer.LayerId} targets layer {relationship.RelatedLayerId}, which the map does not publish.");
                }

                if (byId[relationship.RelatedLayerId].Kind != MapLayerKind.Feature)
                {
                    throw SpatialException.BadArguments(
                        $"Map '{map.Name}' relationship '{relationship.Name}' on layer {layer.LayerId} targets layer {relationship.RelatedLayerId}, which is not a feature layer.");
                }

                foreach (var column in new[] { relationship.PrimaryKeyColumn, relationship.RelatedKeyColumn })
                {
                    if (!IsValidColumn(column))
                    {
                        throw SpatialException.BadArguments(
                            $"Map '{map.Name}' relationship '{relationship.Name}' names column '{column}': expected an identifier [A-Za-z_][A-Za-z0-9_]*.");
                    }
                }

                if (!Enum.IsDefined(relationship.Cardinality))
                {
                    throw SpatialException.BadArguments(
                        $"Map '{map.Name}' relationship '{relationship.Name}' has unknown cardinality {relationship.Cardinality}.");
                }

                ValidateJoin(map, layer, relationship);
            }
        }
    }

    private static void ValidateJoin(Map map, MapLayer layer, LayerRelationship relationship)
    {
        var many = relationship.Cardinality == LayerRelationshipCardinality.ManyToMany;
        if (many && relationship.Join is null)
        {
            throw SpatialException.BadArguments(
                $"Map '{map.Name}' relationship '{relationship.Name}' is many-to-many and needs a join dataset.");
        }

        if (!many)
        {
            if (relationship.Join is { } unexpected)
            {
                throw SpatialException.BadArguments(
                    $"Map '{map.Name}' relationship '{relationship.Name}' is {relationship.Cardinality} and cannot name the join dataset '{unexpected.Dataset}'.");
            }

            return;
        }

        var join = relationship.Join!;
        if (!DatasetPattern.IsMatch(join.Dataset ?? string.Empty))
        {
            throw SpatialException.BadArguments(
                $"Map '{map.Name}' relationship '{relationship.Name}' has join dataset '{join.Dataset}': expected schema.table with only [a-z0-9_].");
        }

        foreach (var column in new[] { join.PrimaryKeyColumn, join.RelatedKeyColumn })
        {
            if (!IsValidColumn(column))
            {
                throw SpatialException.BadArguments(
                    $"Map '{map.Name}' relationship '{relationship.Name}' names join column '{column}': expected an identifier [A-Za-z_][A-Za-z0-9_]*.");
            }
        }
    }

    private static void ValidateLayer(Map map, MapLayer layer, HashSet<string> keys)
    {
        var dataset = layer.Dataset;
        var pattern = layer.Kind == MapLayerKind.Image ? RasterDatasetPattern : DatasetPattern;
        if (string.IsNullOrEmpty(dataset) || !pattern.IsMatch(dataset))
        {
            var expected = layer.Kind == MapLayerKind.Image
                ? "a raster dataset identifier [A-Za-z_][A-Za-z0-9_.-]*"
                : "schema.table with only [a-z0-9_]";
            throw SpatialException.BadArguments($"Layer dataset '{dataset}' is invalid: expected {expected}.");
        }

        if (!keys.Add($"{layer.Kind}:{layer.Store ?? map.Store}:{dataset}"))
        {
            throw SpatialException.BadArguments($"Map '{map.Name}' lists dataset '{dataset}' more than once.");
        }

        if (layer.Name is { Length: 0 })
        {
            throw SpatialException.BadArguments($"Map '{map.Name}' has a layer with an empty name.");
        }

        ValidateStyle(map, layer);
        ValidateTimeFields(map, layer);
    }

    /// <summary>
    /// Structural designation validation (ADR-0183): the named fields are
    /// ordinary attribute names, the designation is on a feature layer, and two
    /// absent bounds are no designation at all. Whether the fields exist and
    /// are date-typed is a live-schema fact, checked where the map is declared
    /// by <see cref="MapTimeFieldSchemas"/>.
    /// </summary>
    private static void ValidateTimeFields(Map map, MapLayer layer)
    {
        if (layer.TimeFields is not { } designation || designation.IsEmpty)
        {
            return;
        }

        if (layer.Kind != MapLayerKind.Feature)
        {
            throw SpatialException.BadArguments(
                $"Map '{map.Name}' layer {layer.LayerId} designates start/end date fields, but it is an image layer.");
        }

        foreach (var field in new[] { designation.StartField, designation.EndField })
        {
            if (field is not null && !IsValidColumn(field))
            {
                throw SpatialException.BadArguments(
                    $"Map '{map.Name}' layer {layer.LayerId} designates date field '{field}': expected an identifier [A-Za-z_][A-Za-z0-9_]*.");
            }
        }
    }

    /// <summary>
    /// A non-empty layer style must be a JSON array of style-layer objects
    /// (ADR-0047). The renderer still rejects paint it cannot draw; this only
    /// pins the persisted shape so a corrupt style never reaches the file.
    /// </summary>
    private static void ValidateStyle(Map map, MapLayer layer)
    {
        if (string.IsNullOrWhiteSpace(layer.Style))
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(layer.Style);
        }
        catch (JsonException exception)
        {
            throw SpatialException.BadArguments(
                $"Map '{map.Name}' layer '{layer.Dataset}' has a style that is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw SpatialException.BadArguments(
                    $"Map '{map.Name}' layer '{layer.Dataset}' style must be an array of style layers.");
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw SpatialException.BadArguments(
                        $"Map '{map.Name}' layer '{layer.Dataset}' style entries must be JSON objects.");
                }
            }
        }
    }

    private static int AssignId(Map map, MapLayer layer, HashSet<int> ids, ref int next)
    {
        var id = layer.LayerId < 0 ? next++ : layer.LayerId;
        if (!ids.Add(id))
        {
            throw SpatialException.BadArguments($"Map '{map.Name}' assigns layer id {id} more than once.");
        }

        return id;
    }

    /// <summary>Whether a name is a valid flat map name.</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);
}
