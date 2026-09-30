using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Core;
using Spatial.Stores.PostGIS.Data;

namespace Spatial.Stores.PostGIS;

/// <summary>
/// The delete path of the feature-editing face (ADR-0037): one
/// <c>DELETE</c> per requested identity, built from the discovered identity
/// columns, and one <see cref="FeatureEditOutcome"/> per feature — the rows
/// it affected, or a typed failure for that feature alone. Split out of
/// <see cref="PostgisEditStore"/> so that class keeps the batch, session and
/// transaction lifecycle and the per-feature statements sit with the outcomes
/// they produce.
/// </summary>
internal static class PostgisFeatureDeletes
{
    /// <summary>Deletes every identity, in request order, on the session's transaction.</summary>
    public static async Task<IReadOnlyList<FeatureEditOutcome>> DeleteAsync(
        PostgisEditSession session,
        PostgisDatasetName name,
        DatasetDescription description,
        IReadOnlyList<FeatureId> featureIds,
        bool byteOrderText,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<FeatureEditOutcome>(featureIds.Count);
        foreach (var id in featureIds)
        {
            outcomes.Add(await DeleteFeatureAsync(session, name, description, id, byteOrderText, cancellationToken));
        }

        return outcomes;
    }

    private static async Task<FeatureEditOutcome> DeleteFeatureAsync(
        PostgisEditSession session,
        PostgisDatasetName name,
        DatasetDescription description,
        FeatureId id,
        bool byteOrderText,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = session.CreateCommand(
                PostgisQueries.Delete(name, description.Schema, description.IdColumns, byteOrderText),
                PostgisIdentity.Values(description, id));
            return PostgisEditOutcomes.Affected(id, await command.ExecuteNonQueryAsync(cancellationToken));
        }
        catch (Exception exception) when (PostgisEditOutcomes.IsFeatureFailure(exception))
        {
            return PostgisEditOutcomes.FailureFor(id, exception);
        }
    }
}
