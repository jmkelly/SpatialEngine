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
    public static Task<IResult> EditsAsync(
        EsriEditOperation operation,
        DatasetDescription dataset,
        IFeatureStore store,
        IFeatureEditStore editStore,
        EsriEditRequest request,
        CoordinateReference? layerCrs,
        CancellationToken cancellationToken) =>
        EditsAsync(new EditInvocation(operation, dataset, store, editStore, request, layerCrs), cancellationToken);

    /// <summary>Executes the requested editing operation and writes its per-feature results.</summary>
    public static async Task<IResult> EditsAsync(EditInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scheme = EsriObjectIdScheme.For(invocation.Dataset);
        if (!scheme.SupportsEditing)
        {
            throw EsriInteropException.Invalid(
                $"Layer '{invocation.Dataset.Id}' has no integer identity column, so it cannot be edited.");
        }

        var results = await RunAsync(invocation.Operation, EditSession.Start(invocation, scheme, cancellationToken), invocation.Request);
        return WriteEdits(invocation.Operation, results);
    }

    private static async Task<EditResults> RunAsync(EsriEditOperation operation, EditSession session, EsriEditRequest request)
    {
        session.ThrowIfCancelled();
        var transactions = request.RollbackOnFailure ? session.Store as ITransactionStore : null;
        var handle = transactions is null ? null : await transactions.BeginAsync(session.CancellationToken);
        var active = handle is null ? session : session.WithTransaction(handle);
        try
        {
            var results = new EditResults();
            await RunAddsAsync(operation, active, request, results);
            await RunUpdatesAsync(operation, active, request, results);
            await RunDeletesAsync(operation, active, request, results);
            await FinishAsync(transactions, handle, results, session.CancellationToken);
            return results;
        }
        catch
        {
            if (transactions is not null && handle is not null)
            {
                await SafeRollbackAsync(transactions, handle, session.CancellationToken);
            }

            throw;
        }
    }

    private static async Task RunAddsAsync(EsriEditOperation operation, EditSession session, EsriEditRequest request, EditResults results)
    {
        if (operation is EsriEditOperation.Add or EsriEditOperation.Apply)
        {
            results.Adds = await AddRangeAsync(session, request.Adds);
        }
    }

    private static async Task RunUpdatesAsync(EsriEditOperation operation, EditSession session, EsriEditRequest request, EditResults results)
    {
        if (operation is EsriEditOperation.Update or EsriEditOperation.Apply)
        {
            results.Updates = await UpdateRangeAsync(session, request.Updates);
        }
    }

    private static async Task RunDeletesAsync(EsriEditOperation operation, EditSession session, EsriEditRequest request, EditResults results)
    {
        if (operation is EsriEditOperation.Delete or EsriEditOperation.Apply)
        {
            results.Deletes = await DeleteRangeAsync(session, request);
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

    private static async Task<List<EsriEditResult>> AddRangeAsync(EditSession session, IReadOnlyList<JsonElement> adds)
    {
        session.ThrowIfCancelled();
        var results = new EsriEditResult?[adds.Count];
        var features = new List<Feature>(adds.Count);
        var positions = new List<int>(adds.Count);
        for (var i = 0; i < adds.Count; i++)
        {
            try
            {
                features.Add(BuildAdd(adds[i], session));
                positions.Add(i);
            }
            catch (Exception exception) when (IsFeatureFailure(exception))
            {
                results[i] = ToFailure(exception);
            }
        }

        if (features.Count > 0)
        {
            var outcomes = await session.EditStore.AddAsync(
                session.Dataset.Id, new FeatureBatch((FeatureSchema)session.Dataset.Schema, features), session.Transaction, session.CancellationToken);
            ApplyOutcomes(outcomes, positions, results, session.Scheme);
        }

        return Finalise(results);
    }

    private static async Task<List<EsriEditResult>> UpdateRangeAsync(EditSession session, IReadOnlyList<JsonElement> updates)
    {
        session.ThrowIfCancelled();
        if (updates.Count == 0)
        {
            return [];
        }

        var work = new UpdateWorklist(updates);
        ReadObjectIds(work);

        work.Existing = await ResolveAsync(session, work.Requested);
        BuildUpdates(session, work);

        if (work.Features.Count > 0)
        {
            var outcomes = await session.EditStore.UpdateAsync(
                session.Dataset.Id, new FeatureBatch((FeatureSchema)session.Dataset.Schema, work.Features), session.Transaction, session.CancellationToken);
            ApplyOutcomes(outcomes, work.Positions, work.Results, session.Scheme);
        }

        return Finalise(work.Results);
    }

    /// <summary>Reads the requested OBJECTIDs, recording a failure for each malformed entry.</summary>
    private static void ReadObjectIds(UpdateWorklist work)
    {
        for (var i = 0; i < work.Updates.Count; i++)
        {
            try
            {
                work.ObjectIds[i] = ReadObjectId(work.Updates[i]);
                work.Resolved[i] = true;
                work.Requested.Add(work.ObjectIds[i]);
            }
            catch (Exception exception) when (IsFeatureFailure(exception))
            {
                work.Results[i] = ToFailure(exception);
            }
        }
    }

    /// <summary>Builds the update features, recording a failure for each unresolvable or malformed entry.</summary>
    private static void BuildUpdates(EditSession session, UpdateWorklist work)
    {
        for (var i = 0; i < work.Updates.Count; i++)
        {
            if (!work.Resolved[i])
            {
                continue;
            }

            try
            {
                if (!work.Existing.TryGetValue(work.ObjectIds[i], out var feature))
                {
                    throw EsriInteropException.Invalid($"No feature has OBJECTID {work.ObjectIds[i]} in layer '{session.Dataset.Id}'.");
                }

                work.Features.Add(BuildUpdate(work.Updates[i], feature, session));
                work.Positions.Add(i);
            }
            catch (Exception exception) when (IsFeatureFailure(exception))
            {
                work.Results[i] = ToFailure(exception);
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

    private static async Task<List<EsriEditResult>> DeleteRangeAsync(EditSession session, EsriEditRequest request)
    {
        session.ThrowIfCancelled();
        var targetIds = request.Deletes;
        if (request.DeleteWhere is { } where)
        {
            targetIds = await MatchIdsAsync(session, where);
        }

        var results = new EsriEditResult?[targetIds.Count];
        if (targetIds.Count == 0)
        {
            return [];
        }

        var existing = await ResolveAsync(session, targetIds);
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
                    $"No feature has OBJECTID {targetIds[i]} in layer '{session.Dataset.Id}'."));
            }
        }

        if (ids.Count > 0)
        {
            var outcomes = await session.EditStore.DeleteAsync(session.Dataset.Id, ids, session.Transaction, session.CancellationToken);
            ApplyOutcomes(outcomes, positions, results, session.Scheme);
        }

        return Finalise(results);
    }

    private static async Task<List<long>> MatchIdsAsync(EditSession session, EsriFilterClause where)
    {
        var batches = await session.Store.ScanAsync(session.Dataset.Id, session.CancellationToken);
        var ids = new List<long>();
        long ordinal = 0;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            session.ThrowIfCancelled();
            ordinal++;
            if (!session.Scheme.TryResolve(feature, ordinal, out var objectId))
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

    private static async Task<Dictionary<long, Feature>> IndexAsync(EditSession session)
    {
        var batches = await session.Store.ScanAsync(session.Dataset.Id, session.CancellationToken);
        var index = new Dictionary<long, Feature>();
        long ordinal = 0;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            session.ThrowIfCancelled();
            ordinal++;
            if (session.Scheme.TryResolve(feature, ordinal, out var objectId))
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
    private static async Task<Dictionary<long, Feature>> ResolveAsync(EditSession session, IReadOnlyList<long> objectIds)
    {
        if (session.Store is not IFeatureLookup lookup)
        {
            return await IndexAsync(session);
        }

        var distinct = objectIds.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return [];
        }

        var found = await lookup.GetAsync(session.Dataset.Id, distinct.Select(EsriObjectIdScheme.ToFeatureId).ToArray(), session.CancellationToken);
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

    private static Feature BuildAdd(JsonElement element, EditSession session)
    {
        var schema = session.Dataset.Schema;
        var attributes = Property(element, "attributes");
        var geometry = Property(element, "geometry");
        var geometryIndex = FeatureGeometry.Index(schema);
        var values = new AttributeValue[schema.Count];
        var unassigned = false;
        for (var i = 0; i < schema.Count; i++)
        {
            if (i == geometryIndex)
            {
                values[i] = ReadGeometry(geometry, session.LayerCrs);
                continue;
            }

            var isIdentity = session.Dataset.IdColumns.Contains(schema[i].Name, StringComparer.Ordinal);
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

    private static Feature BuildUpdate(JsonElement element, Feature existing, EditSession session)
    {
        var schema = (FeatureSchema)session.Dataset.Schema;
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
                    values[i] = AttributeValue.FromGeometry(EsriGeometryCodec.Decode(geometry, session.LayerCrs));
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

    /// <summary>
    /// One Feature Service edit invocation: which operation runs against
    /// which layer, store and request. The parameter object keeps the engine
    /// entry point readable now that the session carries the rest.
    /// </summary>
    internal sealed record EditInvocation(
        EsriEditOperation Operation,
        DatasetDescription Dataset,
        IFeatureStore Store,
        IFeatureEditStore EditStore,
        EsriEditRequest Request,
        CoordinateReference? LayerCrs);

    /// <summary>
    /// The ambient state one edit runs with: the layer, its stores, the
    /// object-id scheme, the open transaction (when the request asked for
    /// <c>rollbackOnFailure</c> and the store supports it) and the caller's
    /// cancellation token. Threading one value instead of seven parameters
    /// keeps each range builder small enough to read at a glance.
    /// </summary>
    private sealed class EditSession
    {
        private EditSession(
            DatasetDescription dataset,
            IFeatureStore store,
            IFeatureEditStore editStore,
            EsriObjectIdScheme scheme,
            CoordinateReference? layerCrs,
            string? transaction,
            CancellationToken cancellationToken)
        {
            Dataset = dataset;
            Store = store;
            EditStore = editStore;
            Scheme = scheme;
            LayerCrs = layerCrs;
            Transaction = transaction;
            CancellationToken = cancellationToken;
        }

        public DatasetDescription Dataset { get; }

        public IFeatureStore Store { get; }

        public IFeatureEditStore EditStore { get; }

        public EsriObjectIdScheme Scheme { get; }

        public CoordinateReference? LayerCrs { get; }

        public string? Transaction { get; }

        public CancellationToken CancellationToken { get; }

        public static EditSession Start(EditInvocation invocation, EsriObjectIdScheme scheme, CancellationToken cancellationToken) =>
            new(invocation.Dataset, invocation.Store, invocation.EditStore, scheme, invocation.LayerCrs, null, cancellationToken);

        /// <summary>The same session inside the store transaction the batch runs under.</summary>
        public EditSession WithTransaction(string? handle) =>
            new(Dataset, Store, EditStore, Scheme, LayerCrs, handle, CancellationToken);

        /// <summary>Aborts the edit promptly when the caller has gone away.</summary>
        public void ThrowIfCancelled() => CancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// The mutable accumulator an update batch builds: the requested
    /// OBJECTIDs, the resolved features and the per-position outputs.
    /// Grouping them keeps <c>BuildUpdates</c> to two parameters.
    /// </summary>
    private sealed class UpdateWorklist
    {
        public UpdateWorklist(IReadOnlyList<JsonElement> updates)
        {
            Updates = updates;
            ObjectIds = new long[updates.Count];
            Resolved = new bool[updates.Count];
            Requested = new List<long>(updates.Count);
            Features = new List<Feature>(updates.Count);
            Positions = new List<int>(updates.Count);
            Results = new EsriEditResult?[updates.Count];
        }

        public IReadOnlyList<JsonElement> Updates { get; }

        public long[] ObjectIds { get; }

        public bool[] Resolved { get; }

        public List<long> Requested { get; }

        public Dictionary<long, Feature> Existing { get; set; } = [];

        public List<Feature> Features { get; }

        public List<int> Positions { get; }

        public EsriEditResult?[] Results { get; }
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
