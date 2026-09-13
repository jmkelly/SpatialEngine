using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Applies the Feature Service editing operations (spec §9.1.6–§9.1.9):
/// <c>addFeatures</c>, <c>updateFeatures</c>, <c>deleteFeatures</c> and
/// <c>applyEdits</c>. Editing runs only for layers whose dataset has an
/// integer identity column and whose store implements
/// <see cref="IFeatureEditStore"/> (ADR-0037); partial updates and per-object
/// deletes resolve their targets through <see cref="IFeatureLookup"/> when the
/// store provides it (ADR-0038). Split out of <see cref="FeatureService"/> so
/// the facade stays a thin per-operation surface (ADR-0040).
/// </summary>
internal static class FeatureEditEngine
{
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

        var results = await RunAsync(operation, dataset, store, editStore, scheme, request, layerCrs, cancellationToken);
        return WriteEdits(operation, results);
    }

    private static async Task<EditResults> RunAsync(
        EsriEditOperation operation,
        DatasetDescription dataset,
        IFeatureStore store,
        IFeatureEditStore editStore,
        EsriObjectIdScheme scheme,
        EsriEditRequest request,
        CoordinateReference? layerCrs,
        CancellationToken cancellationToken)
    {
        var transactions = request.RollbackOnFailure ? store as ITransactionStore : null;
        var handle = transactions is null ? null : await transactions.BeginAsync(cancellationToken);
        try
        {
            var results = new EditResults();
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

            await FinishAsync(transactions, handle, results, cancellationToken);
            return results;
        }
        catch
        {
            if (transactions is not null && handle is not null)
            {
                await SafeRollbackAsync(transactions, handle, cancellationToken);
            }

            throw;
        }
    }

    private static async Task FinishAsync(
        ITransactionStore? transactions, string? handle, EditResults results, CancellationToken cancellationToken)
    {
        if (transactions is null || handle is null)
        {
            return;
        }

        if (results.HasFailure)
        {
            await transactions.RollbackAsync(handle, cancellationToken);
            results.MarkRolledBack();
        }
        else
        {
            await transactions.CommitAsync(handle, cancellationToken);
        }
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
                features.Add(BuildAdd(adds[i], dataset, layerCrs));
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
            ApplyOutcomes(outcomes, positions, results, scheme);
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
        if (updates.Count == 0)
        {
            return [];
        }

        var results = new EsriEditResult?[updates.Count];
        var objectIds = new long[updates.Count];
        var resolved = new bool[updates.Count];
        var requested = new List<long>(updates.Count);
        ReadObjectIds(updates, requested, objectIds, resolved, results);

        var existing = await ResolveAsync(dataset, store, scheme, requested, cancellationToken);
        var features = new List<Feature>(updates.Count);
        var positions = new List<int>(updates.Count);
        BuildUpdates(updates, existing, objectIds, resolved, dataset, layerCrs, features, positions, results);

        if (features.Count > 0)
        {
            var outcomes = await editStore.UpdateAsync(
                dataset.Id, new FeatureBatch((FeatureSchema)dataset.Schema, features), transaction, cancellationToken);
            ApplyOutcomes(outcomes, positions, results, scheme);
        }

        return Finalise(results);
    }

    /// <summary>Reads the requested OBJECTIDs, recording a failure for each malformed entry.</summary>
    private static void ReadObjectIds(
        IReadOnlyList<JsonElement> updates,
        List<long> requested,
        long[] objectIds,
        bool[] resolved,
        EsriEditResult?[] results)
    {
        for (var i = 0; i < updates.Count; i++)
        {
            try
            {
                objectIds[i] = ReadObjectId(updates[i]);
                resolved[i] = true;
                requested.Add(objectIds[i]);
            }
            catch (Exception exception) when (IsFeatureFailure(exception))
            {
                results[i] = ToFailure(exception);
            }
        }
    }

    /// <summary>Builds the update features, recording a failure for each unresolvable or malformed entry.</summary>
    private static void BuildUpdates(
        IReadOnlyList<JsonElement> updates,
        Dictionary<long, Feature> existing,
        long[] objectIds,
        bool[] resolved,
        DatasetDescription dataset,
        CoordinateReference? layerCrs,
        List<Feature> features,
        List<int> positions,
        EsriEditResult?[] results)
    {
        for (var i = 0; i < updates.Count; i++)
        {
            if (!resolved[i])
            {
                continue;
            }

            try
            {
                if (!existing.TryGetValue(objectIds[i], out var feature))
                {
                    throw EsriInteropException.Invalid($"No feature has OBJECTID {objectIds[i]} in layer '{dataset.Id}'.");
                }

                features.Add(BuildUpdate(updates[i], feature, dataset.Schema, layerCrs));
                positions.Add(i);
            }
            catch (Exception exception) when (IsFeatureFailure(exception))
            {
                results[i] = ToFailure(exception);
            }
        }
    }

    /// <summary>Writes each store outcome back into its request position.</summary>
    private static void ApplyOutcomes(
        IReadOnlyList<FeatureEditOutcome> outcomes, List<int> positions, EsriEditResult?[] results, EsriObjectIdScheme scheme)
    {
        for (var j = 0; j < outcomes.Count; j++)
        {
            results[positions[j]] = ToResult(outcomes[j], scheme);
        }
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

        var existing = await ResolveAsync(dataset, store, scheme, targetIds, cancellationToken);
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
            ApplyOutcomes(outcomes, positions, results, scheme);
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
            if (!scheme.TryResolve(feature, ordinal, out var objectId))
            {
                continue;
            }

            if (where.Matches(feature, new EsriSyntheticField(EsriLayerModel.ObjectIdField, AttributeValue.FromInt64(objectId))))
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

    /// <summary>
    /// Resolves the requested OBJECTIDs to their features (ADR-0038). When
    /// the store advertises <see cref="IFeatureLookup"/> the requested
    /// identities are fetched in one targeted read; otherwise the dataset is
    /// scanned and indexed exactly as before.
    /// </summary>
    private static async Task<Dictionary<long, Feature>> ResolveAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        EsriObjectIdScheme scheme,
        IReadOnlyList<long> objectIds,
        CancellationToken cancellationToken)
    {
        if (store is not IFeatureLookup lookup)
        {
            return await IndexAsync(dataset, store, scheme, cancellationToken);
        }

        var distinct = objectIds.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return [];
        }

        var found = await lookup.GetAsync(dataset.Id, distinct.Select(EsriObjectIdScheme.ToFeatureId).ToArray(), cancellationToken);
        var byId = new Dictionary<FeatureId, Feature>(found.Count);
        foreach (var feature in found)
        {
            byId[feature.Id] = feature;
        }

        var index = new Dictionary<long, Feature>(distinct.Length);
        foreach (var objectId in distinct)
        {
            if (byId.TryGetValue(EsriObjectIdScheme.ToFeatureId(objectId), out var feature))
            {
                index[objectId] = feature;
            }
        }

        return index;
    }

    private static Feature BuildAdd(JsonElement element, DatasetDescription dataset, CoordinateReference? layerCrs)
    {
        var schema = dataset.Schema;
        var attributes = Property(element, "attributes");
        var geometry = Property(element, "geometry");
        var geometryIndex = FeatureGeometry.Index(schema);
        var values = new AttributeValue[schema.Count];
        var unassigned = false;
        for (var i = 0; i < schema.Count; i++)
        {
            if (i == geometryIndex)
            {
                values[i] = ReadGeometry(geometry, layerCrs);
                continue;
            }

            var isIdentity = dataset.IdColumns.Contains(schema[i].Name, StringComparer.Ordinal);
            if (isIdentity && !HasValue(attributes, schema[i].Name))
            {
                // The client omitted OBJECTID: a placeholder the store replaces with
                // its assigned identity (ADR-0043).
                values[i] = AttributeValue.FromInt64(0);
                unassigned = true;
                continue;
            }

            values[i] = EsriAttributeCodec.Read(attributes, schema[i]);
        }

        var id = unassigned ? FeatureId.Unassigned : new FeatureId(Placeholder());
        return new Feature(id, schema, values);
    }

    /// <summary>Whether the Esri attributes carry a non-null value for the named field.</summary>
    private static bool HasValue(JsonElement attributes, string name) =>
        attributes.ValueKind == JsonValueKind.Object
        && attributes.TryGetProperty(name, out var value)
        && value.ValueKind != JsonValueKind.Null;

    private static Feature BuildUpdate(JsonElement element, Feature existing, FeatureSchema schema, CoordinateReference? layerCrs)
    {
        var attributes = Property(element, "attributes");
        var geometry = Property(element, "geometry");
        var geometryIndex = FeatureGeometry.Index(schema);
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
