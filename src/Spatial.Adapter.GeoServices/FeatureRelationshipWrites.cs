using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The relate and unrelate writes (spec §9.1.10/§9.1.11, ADR-0077): they
/// move the same key a traversal reads, so a related record the read cannot
/// see is a record the write cannot have moved. A one-to-one or one-to-many
/// declaration sets the related layer's key column; a many-to-many one adds
/// or removes a join row, because the relationship lives in the join dataset
/// rather than on either layer. Both are edits, so both need the
/// <see cref="IFeatureEditStore"/> face of the store holding the record being
/// changed, and both report one result per origin/related pair the
/// <c>applyEdits</c> way, so a partial failure never hides behind one error.
/// </summary>
internal static class FeatureRelationshipWrites
{
    /// <summary>Relates each <c>relateId</c> to each <c>objectId</c> of the origin layer.</summary>
    public static async Task<IResult> RelateAsync(FeatureRelationshipEditContext request, EsriRequestParameters parameters, CancellationToken cancellationToken) =>
        await ApplyAsync(new RelateCommand(request, true), parameters, cancellationToken);

    /// <summary>Removes the relationship between each <c>objectId</c> and <c>relateId</c>.</summary>
    public static async Task<IResult> UnrelateAsync(FeatureRelationshipEditContext request, EsriRequestParameters parameters, CancellationToken cancellationToken) =>
        await ApplyAsync(new RelateCommand(request, false), parameters, cancellationToken);

    private static async Task<IResult> ApplyAsync(RelateCommand command, EsriRequestParameters parameters, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var target = await FeatureRelationshipTargets.ResolveAsync(
            request.Catalog, request.Registry, request.Service, request.LayerId, parameters.Get("relationshipId"), request.Stores, cancellationToken);
        var origins = await FeatureAttachmentTargets.ResolveAsync(
            target.Origin, target.OriginStore, FeatureAttachmentTargets.ParseIds(parameters.Get("objectIds"), "objectIds"), cancellationToken);
        var relatedIds = FeatureAttachmentTargets.ParseIds(parameters.Get("relateIds"), "relateIds")
            ?? throw GeoServicesErrors.Invalid("The 'relateIds' parameter is required and must be a comma-separated list of integers.");

        var results = new List<EsriRelateResult>();
        foreach (var origin in origins)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = FeatureRelationshipTargets.KeyValue(target.Origin, target.Relationship, origin.Feature)
                ?? throw GeoServicesErrors.Invalid(
                    $"Feature {origin.ObjectId} has no value in key column '{target.Relationship.PrimaryKeyColumn}', so it relates to nothing.");
            foreach (var relatedId in relatedIds)
            {
                results.Add(await ApplyOneAsync(command, target, origin.ObjectId, key, relatedId, cancellationToken));
            }
        }

        return EsriJson.Value(new EsriRelateResponse(target.Relationship.Name, results));
    }

    /// <summary>
    /// One origin/related pair. The pair fails on its own terms — an unknown
    /// related record, a read-only store, a non-nullable key column on
    /// unrelate — and never takes the rest of the batch down with it.
    /// </summary>
    private static async Task<EsriRelateResult> ApplyOneAsync(
        RelateCommand command,
        FeatureRelationshipTargets.RelationshipTarget target,
        long originObjectId,
        AttributeValue key,
        long relatedId,
        CancellationToken cancellationToken)
    {
        try
        {
            var editor = EditStore(command.Request, target.Related.Store);
            return target.Relationship.Join is null
                ? await ApplyDirectAsync(command, target, editor, originObjectId, key, relatedId, cancellationToken)
                : await ApplyManyAsync(command, target, originObjectId, key, relatedId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Failed(originObjectId, relatedId, EsriErrorMapper.EditCodeFor(exception), exception.Message);
        }
    }

    /// <summary>
    /// The one-to-one/one-to-many write: the related record's key column takes
    /// the origin record's key, or is cleared on unrelate — exactly the
    /// equality the traversal filters with.
    /// </summary>
    private static async Task<EsriRelateResult> ApplyDirectAsync(
        RelateCommand command,
        FeatureRelationshipTargets.RelationshipTarget target,
        IFeatureEditStore editor,
        long originObjectId,
        AttributeValue key,
        long relatedId,
        CancellationToken cancellationToken)
    {
        var relationship = target.Relationship;
        var related = target.Related.Description;
        var feature = await FeatureAttachmentTargets.FindFeatureAsync(related, target.RelatedStore, relatedId, cancellationToken);
        var index = KeyIndex(related, relationship.RelatedKeyColumn, relationship.Name, related.Id);
        var current = feature[index];
        if (!command.Relate && !current.Equals(key))
        {
            throw GeoServicesErrors.NotFound(
                $"Feature {relatedId} of layer '{related.Id}' is not related to feature {originObjectId} through '{relationship.Name}'.");
        }

        if (!command.Relate && !related.Schema[index].Nullable)
        {
            throw GeoServicesErrors.Invalid(
                $"Relationship '{relationship.Name}' cannot unrelate feature {relatedId}: key column '{relationship.RelatedKeyColumn}' of layer '{related.Id}' is not nullable.");
        }

        var attributes = feature.Attributes.ToArray();
        attributes[index] = command.Relate ? key : AttributeValue.Null;
        var outcome = await SingleOutcomeAsync(
            editor, related, new Feature(feature.Id, feature.Schema, attributes), cancellationToken);
        return Result(originObjectId, relatedId, outcome);
    }

    /// <summary>
    /// The many-to-many write: a relate adds a join row carrying both keys,
    /// an unrelate deletes the row that carries them. The join store is the
    /// owning layer's, because that is where the traversal reads the join
    /// rows from.
    /// </summary>
    private static async Task<EsriRelateResult> ApplyManyAsync(
        RelateCommand command,
        FeatureRelationshipTargets.RelationshipTarget target,
        long originObjectId,
        AttributeValue key,
        long relatedId,
        CancellationToken cancellationToken)
    {
        var relationship = target.Relationship;
        var join = relationship.Join!;
        var related = target.Related.Description;
        var relatedFeature = await FeatureAttachmentTargets.FindFeatureAsync(related, target.RelatedStore, relatedId, cancellationToken);
        var relatedKey = FeatureRelationshipTargets.RelatedKeyValue(related, relationship, relatedFeature)
            ?? throw GeoServicesErrors.Invalid(
                $"Related feature {relatedId} has no value in key column '{relationship.RelatedKeyColumn}', so it relates to nothing.");

        var stores = command.Request.Stores;
        var joinDescription = await stores.Catalogue(target.OriginStoreKey).DescribeAsync(join.Dataset, cancellationToken);
        var editor = EditStore(command.Request, target.OriginStoreKey);
        return command.Relate
            ? await AddJoinRowAsync(editor, joinDescription, join, key, relatedKey, originObjectId, relatedId, cancellationToken)
            : await DeleteJoinRowAsync(command.Request.Stores, target.OriginStoreKey, editor, joinDescription, join, key, relatedKey, originObjectId, relatedId, cancellationToken);
    }

    private static async Task<EsriRelateResult> AddJoinRowAsync(
        IFeatureEditStore editor,
        DatasetDescription join,
        LayerRelationshipJoin columns,
        AttributeValue originKey,
        AttributeValue relatedKey,
        long originObjectId,
        long relatedId,
        CancellationToken cancellationToken)
    {
        var attributes = new AttributeValue[join.Schema.Count];
        for (var i = 0; i < attributes.Length; i++)
        {
            var field = join.Schema[i];
            attributes[i] = field.Name == columns.PrimaryKeyColumn
                ? originKey
                : field.Name == columns.RelatedKeyColumn
                    ? relatedKey
                    : Identity(join, field.Name)
                        // The store assigns the join row's identity, so the
                        // caller writes the same placeholder the add path
                        // does (ADR-0043).
                        ?? AttributeValue.Null;
        }
        var outcome = await SingleOutcomeAsync(
            editor, join, new Feature(FeatureId.Unassigned, join.Schema, attributes), cancellationToken);
        return Result(originObjectId, relatedId, outcome);
    }

    /// <summary>
    /// The placeholder an unassigned identity column carries, or null when
    /// the column is not an identity column of the dataset.
    /// </summary>
    private static AttributeValue? Identity(DatasetDescription dataset, string column) =>
        dataset.IdColumns.Contains(column, StringComparer.Ordinal) && dataset.Schema[dataset.Schema.IndexOf(column)].Kind == AttributeKind.Int64
            ? AttributeValue.FromInt64(0)
            : null;

    private static async Task<EsriRelateResult> DeleteJoinRowAsync(
        IStoreRegistry stores,
        string originStoreKey,
        IFeatureEditStore editor,
        DatasetDescription join,
        LayerRelationshipJoin columns,
        AttributeValue originKey,
        AttributeValue relatedKey,
        long originObjectId,
        long relatedId,
        CancellationToken cancellationToken)
    {
        var match = FeatureRelationshipKeys.Parse(FeatureRelationshipKeys.Conjoin(
            FeatureRelationshipKeys.Equality(columns.PrimaryKeyColumn, originKey),
            FeatureRelationshipKeys.Equality(columns.RelatedKeyColumn, relatedKey)));
        var rows = await stores.Features(originStoreKey).ScanAsync(join.Id, cancellationToken);
        foreach (var row in rows.SelectMany(batch => batch.Features))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!match.Matches(row))
            {
                continue;
            }

            var deleted = await editor.DeleteAsync(join.Id, [row.Id], cancellationToken: cancellationToken);
            return Result(originObjectId, relatedId, deleted.Count > 0 ? deleted[0] : null);
        }

        throw GeoServicesErrors.NotFound(
            $"The pair ({originKey}, {relatedKey}) is not related through join dataset '{join.Id}', so there is nothing to unrelate.");
    }

    /// <summary>
    /// The keyed editing face of a store, or a typed <c>invalid.arguments</c>
    /// naming it: relate and unrelate are edits, so a read-only store cannot
    /// serve them — the same honesty the attachment writes keep.
    /// </summary>
    private static IFeatureEditStore EditStore(FeatureRelationshipEditContext request, string store) =>
        request.Stores.EditStore(store)
        ?? throw GeoServicesErrors.Invalid($"Store '{store}' exposes no editing face, so relationships cannot be written through it.");

    private static int KeyIndex(DatasetDescription dataset, string column, string relationship, string layer)
    {
        var index = dataset.Schema.IndexOf(column);
        return index >= 0
            ? index
            : throw GeoServicesErrors.Invalid(
                $"Relationship '{relationship}' names related key column '{column}', which layer '{layer}' does not have.");
    }

    private static async Task<FeatureEditOutcome> SingleOutcomeAsync(
        IFeatureEditStore editor, DatasetDescription dataset, Feature feature, CancellationToken cancellationToken)
    {
        var batch = new FeatureBatch(dataset.Schema, [feature]);
        var outcomes = feature.Id == FeatureId.Unassigned
            ? await editor.AddAsync(dataset.Id, batch, cancellationToken: cancellationToken)
            : await editor.UpdateAsync(dataset.Id, batch, cancellationToken: cancellationToken);
        return outcomes.Count > 0
            ? outcomes[0]
            : throw GeoServicesErrors.ServerError($"Store '{dataset.Id}' returned no edit outcome.");
    }

    private static EsriRelateResult Result(long originObjectId, long relatedId, FeatureEditOutcome? outcome) =>
        outcome is null
            ? throw GeoServicesErrors.ServerError("The store returned no edit outcome.")
            : outcome.Succeeded
                ? EsriRelateResult.Succeeded(originObjectId, relatedId)
                : Failed(originObjectId, relatedId, EsriErrorMapper.EditCodeFor(outcome.ErrorCode), outcome.ErrorMessage ?? "The relationship could not be written.");

    private static EsriRelateResult Failed(long originObjectId, long relatedId, int code, string message) =>
        new(originObjectId, relatedId, false, new EsriError(code, message, []));

    /// <summary>One relate or unrelate invocation and whether it relates or unrelates.</summary>
    private sealed record RelateCommand(FeatureRelationshipEditContext Request, bool Relate);
}

/// <summary>Which direction a relationship edit goes.</summary>
internal enum EsriRelationshipOperation
{
    /// <summary>Relate: set the related record's key, or add a join row.</summary>
    Relate,

    /// <summary>Unrelate: clear the related record's key, or delete the join row.</summary>
    Unrelate,
}

/// <summary>
/// The resolved services of one relate/unrelate request: the published
/// service and layer, the operation and the admin gate's inputs. Grouping
/// them keeps the write's signature to the ids it addresses.
/// </summary>
internal sealed record FeatureRelationshipEditContext(
    GeoServicesCatalog Catalog,
    IMapRegistry Registry,
    HttpContext Context,
    string Service,
    int LayerId,
    IStoreRegistry Stores,
    EsriRelationshipOperation Operation,
    IAuthService? Auth,
    bool AuthEnabled,
    string? LegacyToken);

/// <summary>The relate/unrelate response: the relationship and one result per origin/related pair.</summary>
internal sealed record EsriRelateResponse(string Relationship, IReadOnlyList<EsriRelateResult> Results);

/// <summary>One pair's outcome; a failure carries the typed error the store or the declaration produced.</summary>
internal sealed record EsriRelateResult(long ObjectId, long RelateId, bool Success, EsriError? Error = null)
{
    /// <summary>A pair that was related or unrelated.</summary>
    public static EsriRelateResult Succeeded(long objectId, long relateId) => new(objectId, relateId, true);
}
