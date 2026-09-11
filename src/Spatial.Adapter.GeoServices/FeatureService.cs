using System.Globalization;
using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Feature Service (spec §9): the <c>FeatureServer</c> root, layer
/// metadata, <c>query</c> and the editing operations
/// (<c>addFeatures</c>, <c>updateFeatures</c>, <c>deleteFeatures</c>,
/// <c>applyEdits</c>). Features come from the engine's keyed stores; the
/// facade maps them to Esri JSON, applies the supported query/edit subset,
/// and never forwards client text as SQL. Editing is advertised and accepted
/// only for layers whose dataset has an integer identity column and whose
/// store implements <see cref="IFeatureEditStore"/> (ADR-0037); everything
/// else stays read-only.
/// </summary>
internal static class FeatureService
{
    private const double CurrentVersion = 10.0;

    /// <summary>Builds the <c>FeatureServer</c> root (spec §9.0).</summary>
    public static EsriFeatureServerRoot Root(IReadOnlyList<DatasetSummary> datasets, bool editable)
    {
        var layers = datasets.Select((dataset, index) => EsriLayerModel.Reference(index, dataset)).ToArray();
        return new EsriFeatureServerRoot(
            CurrentVersion,
            "SpatialEngine Feature Service",
            false,
            "JSON",
            editable ? EsriLayerModel.EditableCapabilities : EsriLayerModel.ReadOnlyCapabilities,
            EsriLayerModel.MaxRecordCount,
            layers,
            []);
    }

    /// <summary>Builds one layer's metadata (spec §9.1).</summary>
    public static EsriLayer Layer(int layerId, DatasetDescription dataset, bool editable) =>
        EsriLayerModel.Describe(layerId, dataset, editable);

    /// <summary>Executes a query and writes the spec §9.1.4.3 response.</summary>
    public static async Task<IResult> QueryAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        EsriFeatureQuery query,
        IGeometryOperations operations,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var layerCrs = EsriLayerModel.LayerCoordinateReference(dataset.Srid);
        var scheme = EsriObjectIdScheme.For(dataset);
        var queryGeometry = TransformQueryGeometry(query.Geometry, layerCrs, transforms, cancellationToken);
        var matches = await MatchAsync(dataset, store, query, queryGeometry, operations, scheme, cancellationToken);
        if (query.ReturnIdsOnly)
        {
            return IdsOnly(matches);
        }

        if (query.ReturnCountOnly)
        {
            return EsriJson.Value(new EsriCountResponse(matches.Count));
        }

        if (query.ReturnExtentOnly)
        {
            return ExtentOnly(matches, layerCrs, query.OutSr, transforms, cancellationToken);
        }

        if (query.ReturnDistinctValues)
        {
            return DistinctValues(dataset, matches, query, layerCrs);
        }

        var page = Page(matches, query);
        var features = page.Items
            .Select(item => TransformFeature(item, query, layerCrs, transforms, cancellationToken))
            .ToArray();
        return WriteFeatures(dataset, layerCrs, query, features, page.Exceeded);
    }

    /// <summary>Executes the requested editing operation and writes its per-feature results.</summary>
    public static async Task<IResult> EditsAsync(
        EsriEditOperation operation,
        DatasetDescription dataset,
        IFeatureStore store,
        IFeatureEditStore editStore,
        EsriEditRequest request,
        CoordinateReference? layerCrs,
        CancellationToken cancellationToken)
    {
        var scheme = EsriObjectIdScheme.For(dataset);
        if (!scheme.SupportsEditing)
        {
            throw EsriInteropException.Invalid(
                $"Layer '{dataset.Id}' has no integer identity column, so it cannot be edited.");
        }

        var transactions = request.RollbackOnFailure ? store as ITransactionStore : null;
        var handle = transactions is null ? null : await transactions.BeginAsync(cancellationToken);
        var results = new EditResults();
        try
        {
            if (operation is EsriEditOperation.Add or EsriEditOperation.Apply)
            {
                results.Adds = await AddRangeAsync(dataset, editStore, scheme, request.Adds, layerCrs, handle, cancellationToken);
            }

            if (operation is EsriEditOperation.Update or EsriEditOperation.Apply)
            {
                results.Updates = await UpdateRangeAsync(dataset, store, editStore, scheme, request.Updates, layerCrs, handle, cancellationToken);
            }

            if (operation is EsriEditOperation.Delete or EsriEditOperation.Apply)
            {
                results.Deletes = await DeleteRangeAsync(dataset, store, editStore, scheme, request, handle, cancellationToken);
            }

            if (transactions is not null)
            {
                if (results.HasFailure)
                {
                    await transactions.RollbackAsync(handle!, cancellationToken);
                    results.MarkRolledBack();
                }
                else
                {
                    await transactions.CommitAsync(handle!, cancellationToken);
                }
            }
        }
        catch
        {
            if (transactions is not null && handle is not null)
            {
                await SafeRollbackAsync(transactions, handle, cancellationToken);
            }

            throw;
        }

        return WriteEdits(operation, results);
    }

    private static async Task<List<EsriEditResult>> AddRangeAsync(
        DatasetDescription dataset,
        IFeatureEditStore editStore,
        EsriObjectIdScheme scheme,
        IReadOnlyList<JsonElement> adds,
        CoordinateReference? layerCrs,
        string? transaction,
        CancellationToken cancellationToken)
    {
        var results = new EsriEditResult?[adds.Count];
        var features = new List<Feature>(adds.Count);
        var positions = new List<int>(adds.Count);
        for (var i = 0; i < adds.Count; i++)
        {
            try
            {
                features.Add(BuildAdd(adds[i], dataset.Schema, layerCrs));
                positions.Add(i);
            }
            catch (Exception exception) when (IsFeatureFailure(exception))
            {
                results[i] = ToFailure(exception);
            }
        }

        if (features.Count > 0)
        {
            var outcomes = await editStore.AddAsync(
                dataset.Id, new FeatureBatch((FeatureSchema)dataset.Schema, features), transaction, cancellationToken);
            for (var j = 0; j < outcomes.Count; j++)
            {
                results[positions[j]] = ToResult(outcomes[j], scheme);
            }
        }

        return Finalise(results);
    }

    private static async Task<List<EsriEditResult>> UpdateRangeAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        IFeatureEditStore editStore,
        EsriObjectIdScheme scheme,
        IReadOnlyList<JsonElement> updates,
        CoordinateReference? layerCrs,
        string? transaction,
        CancellationToken cancellationToken)
    {
        var results = new EsriEditResult?[updates.Count];
        if (updates.Count == 0)
        {
            return [];
        }

        var existing = await IndexAsync(dataset, store, scheme, cancellationToken);
        var features = new List<Feature>(updates.Count);
        var positions = new List<int>(updates.Count);
        for (var i = 0; i < updates.Count; i++)
        {
            try
            {
                var objectId = ReadObjectId(updates[i]);
                if (!existing.TryGetValue(objectId, out var feature))
                {
                    throw EsriInteropException.Invalid($"No feature has OBJECTID {objectId} in layer '{dataset.Id}'.");
                }

                features.Add(BuildUpdate(updates[i], feature, dataset.Schema, layerCrs));
                positions.Add(i);
            }
            catch (Exception exception) when (IsFeatureFailure(exception))
            {
                results[i] = ToFailure(exception);
            }
        }

        if (features.Count > 0)
        {
            var outcomes = await editStore.UpdateAsync(
                dataset.Id, new FeatureBatch((FeatureSchema)dataset.Schema, features), transaction, cancellationToken);
            for (var j = 0; j < outcomes.Count; j++)
            {
                results[positions[j]] = ToResult(outcomes[j], scheme);
            }
        }

        return Finalise(results);
    }

    private static async Task<List<EsriEditResult>> DeleteRangeAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        IFeatureEditStore editStore,
        EsriObjectIdScheme scheme,
        EsriEditRequest request,
        string? transaction,
        CancellationToken cancellationToken)
    {
        var targetIds = request.Deletes;
        if (request.DeleteWhere is { } where)
        {
            targetIds = await MatchIdsAsync(dataset, store, scheme, where, cancellationToken);
        }

        var results = new EsriEditResult?[targetIds.Count];
        if (targetIds.Count == 0)
        {
            return [];
        }

        var existing = await IndexAsync(dataset, store, scheme, cancellationToken);
        var ids = new List<FeatureId>(targetIds.Count);
        var positions = new List<int>(targetIds.Count);
        for (var i = 0; i < targetIds.Count; i++)
        {
            if (existing.TryGetValue(targetIds[i], out var feature))
            {
                ids.Add(feature.Id);
                positions.Add(i);
            }
            else
            {
                results[i] = ToFailure(EsriInteropException.Invalid(
                    $"No feature has OBJECTID {targetIds[i]} in layer '{dataset.Id}'."));
            }
        }

        if (ids.Count > 0)
        {
            var outcomes = await editStore.DeleteAsync(dataset.Id, ids, transaction, cancellationToken);
            for (var j = 0; j < outcomes.Count; j++)
            {
                results[positions[j]] = ToResult(outcomes[j], scheme);
            }
        }

        return Finalise(results);
    }

    private static async Task<List<long>> MatchIdsAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        EsriObjectIdScheme scheme,
        EsriFilterClause where,
        CancellationToken cancellationToken)
    {
        var batches = await store.ScanAsync(dataset.Id, cancellationToken);
        var ids = new List<long>();
        long ordinal = 0;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            ordinal++;
            if (where.Matches(feature) && scheme.TryResolve(feature, ordinal, out var objectId))
            {
                ids.Add(objectId);
            }
        }

        return ids;
    }

    private static async Task<Dictionary<long, Feature>> IndexAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        EsriObjectIdScheme scheme,
        CancellationToken cancellationToken)
    {
        var batches = await store.ScanAsync(dataset.Id, cancellationToken);
        var index = new Dictionary<long, Feature>();
        long ordinal = 0;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            ordinal++;
            if (scheme.TryResolve(feature, ordinal, out var objectId))
            {
                index[objectId] = feature;
            }
        }

        return index;
    }

    private static Feature BuildAdd(JsonElement element, FeatureSchema schema, CoordinateReference? layerCrs)
    {
        var attributes = Property(element, "attributes");
        var geometry = Property(element, "geometry");
        var geometryIndex = GeometryIndex(schema);
        var values = new AttributeValue[schema.Count];
        for (var i = 0; i < schema.Count; i++)
        {
            values[i] = i == geometryIndex
                ? ReadGeometry(geometry, layerCrs)
                : EsriAttributeCodec.Read(attributes, schema[i]);
        }

        return new Feature(new FeatureId(Placeholder()), schema, values);
    }

    private static Feature BuildUpdate(JsonElement element, Feature existing, FeatureSchema schema, CoordinateReference? layerCrs)
    {
        var attributes = Property(element, "attributes");
        var geometry = Property(element, "geometry");
        var geometryIndex = GeometryIndex(schema);
        var values = existing.Attributes.ToArray();
        for (var i = 0; i < schema.Count; i++)
        {
            if (i == geometryIndex)
            {
                if (geometry.ValueKind == JsonValueKind.Object)
                {
                    values[i] = AttributeValue.FromGeometry(EsriGeometryCodec.Decode(geometry, layerCrs));
                }
                else if (geometry.ValueKind == JsonValueKind.Null)
                {
                    values[i] = AttributeValue.Null;
                }

                continue;
            }

            if (attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty(schema[i].Name, out var attribute))
            {
                values[i] = attribute.ValueKind == JsonValueKind.Null
                    ? AttributeValue.Null
                    : EsriAttributeCodec.Read(attributes, schema[i]);
            }
        }

        return new Feature(existing.Id, schema, values);
    }

    private static AttributeValue ReadGeometry(JsonElement geometry, CoordinateReference? layerCrs) =>
        geometry.ValueKind == JsonValueKind.Object
            ? AttributeValue.FromGeometry(EsriGeometryCodec.Decode(geometry, layerCrs))
            : AttributeValue.Null;

    private static long ReadObjectId(JsonElement element)
    {
        var attributes = Property(element, "attributes");
        if (attributes.ValueKind == JsonValueKind.Object
            && attributes.TryGetProperty(EsriLayerModel.ObjectIdField, out var objectId)
            && objectId.ValueKind == JsonValueKind.Number
            && objectId.TryGetInt64(out var value))
        {
            return value;
        }

        throw EsriInteropException.Invalid($"The feature carries no numeric '{EsriLayerModel.ObjectIdField}' attribute.");
    }

    private static JsonElement Property(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string Placeholder() => Guid.NewGuid().ToString("N");

    private static bool IsFeatureFailure(Exception exception) =>
        exception is EsriInteropException or SpatialException or ArgumentException or InvalidOperationException;

    private static EsriEditResult ToResult(FeatureEditOutcome outcome, EsriObjectIdScheme scheme) =>
        outcome.Succeeded
            ? EsriEditResult.Succeeded(scheme.ResolveAssigned(outcome.Id))
            : EsriEditResult.Failed(
                EsriErrorMapper.EditCodeFor(outcome.ErrorCode),
                outcome.ErrorMessage ?? "The feature edit failed.");

    private static EsriEditResult ToFailure(Exception exception) =>
        EsriEditResult.Failed(EsriErrorMapper.EditCodeFor(exception), exception.Message);

    private static List<EsriEditResult> Finalise(IReadOnlyList<EsriEditResult?> results) =>
        results.Select(result => result ?? EsriEditResult.Failed(EsriErrorCodes.ServerError, "The edit was not attempted.")).ToList();

    private static IResult WriteEdits(EsriEditOperation operation, EditResults results) =>
        EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            if (operation is EsriEditOperation.Add or EsriEditOperation.Apply)
            {
                EsriEditResultCodec.Write(writer, "addResults", results.Adds);
            }

            if (operation is EsriEditOperation.Update or EsriEditOperation.Apply)
            {
                EsriEditResultCodec.Write(writer, "updateResults", results.Updates);
            }

            if (operation is EsriEditOperation.Delete or EsriEditOperation.Apply)
            {
                EsriEditResultCodec.Write(writer, "deleteResults", results.Deletes);
            }

            writer.WriteEndObject();
        });

    private static async Task SafeRollbackAsync(ITransactionStore transactions, string handle, CancellationToken cancellationToken)
    {
        try
        {
            await transactions.RollbackAsync(handle, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The original failure is the actionable one; a rollback failure must not mask it.
        }
    }

    private static async Task<List<MatchedFeature>> MatchAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        EsriFeatureQuery query,
        IGeometry? queryGeometry,
        IGeometryOperations operations,
        EsriObjectIdScheme scheme,
        CancellationToken cancellationToken)
    {
        var batches = await store.ScanAsync(dataset.Id, cancellationToken);
        var matches = new List<MatchedFeature>();
        long ordinal = 0;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            ordinal++;
            if (!scheme.TryResolve(feature, ordinal, out var objectId))
            {
                throw new EsriInteropException(
                    EsriErrorCodes.ServerError,
                    $"The identity column of layer '{dataset.Id}' is not an integer.");
            }

            if (Matches(query, feature, objectId, queryGeometry, operations, cancellationToken))
            {
                matches.Add(new MatchedFeature(objectId, feature));
            }
        }

        return matches;
    }

    private static bool Matches(
        EsriFeatureQuery query,
        Feature feature,
        long objectId,
        IGeometry? queryGeometry,
        IGeometryOperations operations,
        CancellationToken cancellationToken)
    {
        if (query.ObjectIds is { } ids && !ids.Contains(objectId))
        {
            return false;
        }

        if (query.Where is { } where && !where.Matches(feature))
        {
            return false;
        }

        return queryGeometry is null || SpatialMatch(feature, queryGeometry, query.SpatialRel, operations, cancellationToken);
    }

    private static bool SpatialMatch(Feature feature, IGeometry queryGeometry, string spatialRel, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        var geometry = FindGeometry(feature);
        if (geometry is null || geometry.Envelope is not { } featureEnvelope || queryGeometry.Envelope is not { } queryEnvelope)
        {
            return false;
        }

        if (string.Equals(spatialRel, EsriFeatureQuery.EnvelopeIntersects, StringComparison.Ordinal))
        {
            return featureEnvelope.Intersects(queryEnvelope);
        }

        return !operations.Intersection(geometry, queryGeometry, cancellationToken).IsEmpty;
    }

    private static IGeometry? TransformQueryGeometry(
        IGeometry? geometry,
        CoordinateReference? layerCrs,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        if (geometry is null || layerCrs is null || geometry.CoordinateReference is not { } source || source == layerCrs)
        {
            return geometry;
        }

        return transforms.Transform(geometry, source.ToString(), layerCrs.Value.ToString(), cancellationToken);
    }

    private static PageResult Page(List<MatchedFeature> matches, EsriFeatureQuery query)
    {
        var offset = Math.Min(query.ResultOffset ?? 0, matches.Count);
        var count = query.ResultRecordCount ?? EsriLayerModel.MaxRecordCount;
        var items = matches.Skip(offset).Take(count).ToArray();
        return new PageResult(items, offset + items.Length < matches.Count);
    }

    private static MatchedFeature TransformFeature(
        MatchedFeature match,
        EsriFeatureQuery query,
        CoordinateReference? layerCrs,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var feature = match.Feature;
        if (query.OutSr is not { } target || layerCrs is null || target == layerCrs)
        {
            return match;
        }

        var geometryIndex = GeometryIndex(feature.Schema);
        if (geometryIndex < 0 || feature[geometryIndex].Kind != AttributeKind.Geometry)
        {
            return match;
        }

        var transformed = transforms.Transform(feature[geometryIndex].GeometryValue, layerCrs.Value.ToString(), target.ToString(), cancellationToken);
        var attributes = feature.Attributes.ToArray();
        attributes[geometryIndex] = AttributeValue.FromGeometry(transformed);
        return new MatchedFeature(match.ObjectId, new Feature(feature.Id, feature.Schema, attributes));
    }

    private static IResult IdsOnly(List<MatchedFeature> matches) =>
        EsriJson.Value(new EsriObjectIdsResponse(EsriLayerModel.ObjectIdField, matches.Select(match => match.ObjectId).ToArray()));

    /// <summary>
    /// The <c>returnExtentOnly</c> response: the envelope of the full matched
    /// set (before paging), in <c>outSR</c> when supplied, else the layer SR.
    /// A matchless query yields <c>"extent": null</c>.
    /// </summary>
    private static IResult ExtentOnly(
        IReadOnlyList<MatchedFeature> matches,
        CoordinateReference? layerCrs,
        CoordinateReference? outSr,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var extent = Envelope.Empty;
        foreach (var match in matches)
        {
            if (FindGeometry(match.Feature) is not { } geometry)
            {
                continue;
            }

            var projected = TransformGeometry(geometry, layerCrs, outSr, transforms, cancellationToken);
            if (projected.Envelope is { } envelope)
            {
                extent = extent.Union(envelope);
            }
        }

        return WriteExtent(extent, outSr ?? layerCrs);
    }

    private static IResult WriteExtent(Envelope extent, CoordinateReference? coordinateReference) =>
        EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            if (extent.IsEmpty)
            {
                writer.WriteNull("extent");
            }
            else
            {
                writer.WritePropertyName("extent");
                writer.WriteStartObject();
                writer.WriteNumber("xmin", extent.MinX);
                writer.WriteNumber("ymin", extent.MinY);
                writer.WriteNumber("xmax", extent.MaxX);
                writer.WriteNumber("ymax", extent.MaxY);
                WriteSpatialReference(writer, coordinateReference);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        });

    /// <summary>
    /// The <c>returnDistinctValues</c> response: the deduplicated combinations
    /// of the projected fields, no geometry. Paging is applied after dedupe.
    /// </summary>
    private static IResult DistinctValues(
        DatasetDescription dataset,
        IReadOnlyList<MatchedFeature> matches,
        EsriFeatureQuery query,
        CoordinateReference? layerCrs)
    {
        var fields = ResolveDistinctFields(dataset, query.OutFields);
        var rows = new List<AttributeValue[]>();
        var seen = new HashSet<AttributeValue[]>(AttributeRowComparer.Instance);
        foreach (var match in matches)
        {
            var row = new AttributeValue[fields.Count];
            for (var i = 0; i < fields.Count; i++)
            {
                row[i] = match.Feature[fields[i].Index];
            }

            if (seen.Add(row))
            {
                rows.Add(row);
            }
        }

        var offset = Math.Min(query.ResultOffset ?? 0, rows.Count);
        var count = query.ResultRecordCount ?? EsriLayerModel.MaxRecordCount;
        var page = rows.Skip(offset).Take(count).ToArray();
        return WriteDistinctValues(dataset, query.OutSr ?? layerCrs, fields, page, offset + page.Length < rows.Count);
    }

    private static List<DistinctField> ResolveDistinctFields(DatasetDescription dataset, IReadOnlyList<string>? outFields)
    {
        var schema = dataset.Schema;
        var names = outFields is { Count: > 0 }
            ? outFields
            : schema.Fields.Where(field => field.Kind != AttributeKind.Geometry).Select(field => field.Name).ToArray();
        var fields = new List<DistinctField>(names.Count);
        foreach (var name in names)
        {
            var index = schema.IndexOf(name);
            if (index < 0 || schema[index].Kind == AttributeKind.Geometry)
            {
                throw EsriInteropException.Invalid(
                    $"The 'outFields' value '{name}' is not a distinctable attribute of layer '{dataset.Id}'.");
            }

            fields.Add(new DistinctField(name, index));
        }

        return fields;
    }

    private static IResult WriteDistinctValues(
        DatasetDescription dataset,
        CoordinateReference? coordinateReference,
        IReadOnlyList<DistinctField> fields,
        IReadOnlyList<AttributeValue[]> rows,
        bool exceeded)
    {
        return EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("objectIdFieldName", EsriLayerModel.ObjectIdField);
            writer.WriteString("geometryType", EsriLayerModel.GeometryType(dataset.GeometryType));
            WriteSpatialReference(writer, coordinateReference);
            WriteFields(writer, dataset);
            writer.WritePropertyName("features");
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("attributes");
                writer.WriteStartObject();
                for (var i = 0; i < fields.Count; i++)
                {
                    EsriAttributeCodec.Write(writer, fields[i].Name, row[i]);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("exceededTransferLimit", exceeded);
            writer.WriteEndObject();
        });
    }

    private static IGeometry TransformGeometry(
        IGeometry geometry,
        CoordinateReference? source,
        CoordinateReference? target,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        if (target is not { } to || source is not { } from || from == to)
        {
            return geometry;
        }

        return transforms.Transform(geometry, from.ToString(), to.ToString(), cancellationToken);
    }

    private static IResult WriteFeatures(
        DatasetDescription dataset,
        CoordinateReference? layerCrs,
        EsriFeatureQuery query,
        IReadOnlyList<MatchedFeature> features,
        bool exceeded)
    {
        var options = new EsriFeatureWriteOptions(EsriLayerModel.ObjectIdField, 0, query.OutFields, query.ReturnGeometry);
        return EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("objectIdFieldName", EsriLayerModel.ObjectIdField);
            writer.WriteString("geometryType", EsriLayerModel.GeometryType(dataset.GeometryType));
            WriteSpatialReference(writer, query.OutSr ?? layerCrs);
            WriteFields(writer, dataset);
            writer.WritePropertyName("features");
            writer.WriteStartArray();
            foreach (var feature in features)
            {
                EsriFeatureCodec.Write(writer, feature.Feature, options with { ObjectId = feature.ObjectId });
            }

            writer.WriteEndArray();
            writer.WriteBoolean("exceededTransferLimit", exceeded);
            writer.WriteEndObject();
        });
    }

    private static void WriteSpatialReference(Utf8JsonWriter writer, CoordinateReference? coordinateReference)
    {
        if (coordinateReference is { } crs && IsMapped(crs))
        {
            EsriSpatialReference.Write(writer, crs);
            return;
        }

        writer.WriteNull("spatialReference");
    }

    private static bool IsMapped(CoordinateReference crs) =>
        string.Equals(crs.Authority, "EPSG", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(crs.Code, NumberStyles.None, CultureInfo.InvariantCulture, out var epsg)
        && WkidMap.TryFromEpsg(epsg, out _);

    private static void WriteFields(Utf8JsonWriter writer, DatasetDescription dataset)
    {
        writer.WritePropertyName("fields");
        writer.WriteStartArray();
        WriteField(writer, EsriLayerModel.ObjectIdField, EsriFieldType.Oid, false, false);
        foreach (var field in dataset.Schema.Fields)
        {
            WriteField(writer, field.Name, EsriFieldType.FromAttributeKind(field.Kind), field.Nullable, field.Kind != AttributeKind.Geometry);
        }

        writer.WriteEndArray();
    }

    private static void WriteField(Utf8JsonWriter writer, string name, string type, bool nullable, bool editable)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("type", type);
        writer.WriteString("alias", name);
        writer.WriteBoolean("nullable", nullable);
        writer.WriteBoolean("editable", editable);
        writer.WriteEndObject();
    }

    private static IGeometry? FindGeometry(Feature feature)
    {
        var index = GeometryIndex(feature.Schema);
        return index >= 0 && feature[index].Kind == AttributeKind.Geometry ? feature[index].GeometryValue : null;
    }

    private static int GeometryIndex(FeatureSchema schema)
    {
        for (var i = 0; i < schema.Count; i++)
        {
            if (schema[i].Kind == AttributeKind.Geometry)
            {
                return i;
            }
        }

        return -1;
    }

    private sealed record MatchedFeature(long ObjectId, Feature Feature);

    private sealed record PageResult(IReadOnlyList<MatchedFeature> Items, bool Exceeded);

    /// <summary>One projected field of a distinct-values request.</summary>
    private readonly record struct DistinctField(string Name, int Index);

    /// <summary>Structural equality for projected distinct-value rows.</summary>
    private sealed class AttributeRowComparer : IEqualityComparer<AttributeValue[]>
    {
        public static AttributeRowComparer Instance { get; } = new();

        public bool Equals(AttributeValue[]? left, AttributeValue[]? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null || right is null || left.Length != right.Length)
            {
                return false;
            }

            for (var i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(AttributeValue[] row)
        {
            var hash = new HashCode();
            foreach (var value in row)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }

    private sealed class EditResults
    {
        public List<EsriEditResult> Adds { get; set; } = [];

        public List<EsriEditResult> Updates { get; set; } = [];

        public List<EsriEditResult> Deletes { get; set; } = [];

        public bool HasFailure =>
            Adds.Any(result => !result.Success) || Updates.Any(result => !result.Success) || Deletes.Any(result => !result.Success);

        /// <summary>Reports a rolled-back batch: every success becomes a failure with the rollback reason.</summary>
        public void MarkRolledBack()
        {
            Adds = RolledBack(Adds);
            Updates = RolledBack(Updates);
            Deletes = RolledBack(Deletes);
        }

        private static List<EsriEditResult> RolledBack(List<EsriEditResult> results) =>
            results
                .Select(result => result.Success
                    ? EsriEditResult.Failed(EsriErrorCodes.InvalidParameters, "The edit was rolled back because another feature in the batch failed.")
                    : result)
                .ToList();
    }
}

/// <summary>Which Feature Service editing operation a request targets.</summary>
internal enum EsriEditOperation
{
    Add,
    Update,
    Delete,
    Apply,
}

/// <summary>The <c>returnIdsOnly</c> response (spec §9.1.4.4).</summary>
internal sealed record EsriObjectIdsResponse(string ObjectIdFieldName, IReadOnlyList<long> ObjectIds);

/// <summary>The <c>returnCountOnly</c> response (10.x addition).</summary>
internal sealed record EsriCountResponse(int Count);
