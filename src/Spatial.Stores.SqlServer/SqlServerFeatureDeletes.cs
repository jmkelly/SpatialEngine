using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.SqlServer.Core;
using Spatial.Stores.SqlServer.Data;

namespace Spatial.Stores.SqlServer;

/// <summary>
/// The delete path of the feature-editing face (ADR-0037): one
/// <c>DELETE</c> per requested identity, built from the discovered identity
/// columns, and one <see cref="FeatureEditOutcome"/> per feature — the rows
/// it affected, or a typed failure for that feature alone. Split out of
/// <see cref="SqlServerEditStore"/> so that class keeps the batch, session and
/// transaction lifecycle and the per-feature statements sit with the outcomes
/// they produce.
/// </summary>
internal static class SqlServerFeatureDeletes
{
    /// <summary>Deletes every identity, in request order, on the session's transaction.</summary>
    public static async Task<IReadOnlyList<FeatureEditOutcome>> DeleteAsync(
        SqlServerEditSession session,
        SqlServerDatasetName name,
        DatasetDescription description,
        IReadOnlyList<FeatureId> featureIds,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<FeatureEditOutcome>(featureIds.Count);
        foreach (var id in featureIds)
        {
            outcomes.Add(await DeleteFeatureAsync(session, name, description, id, cancellationToken));
        }

        return outcomes;
    }

    private static async Task<FeatureEditOutcome> DeleteFeatureAsync(
        SqlServerEditSession session,
        SqlServerDatasetName name,
        DatasetDescription description,
        FeatureId id,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = session.CreateCommand(
                SqlServerQueries.Delete(name, description.IdColumns), SqlServerIdentity.Values(description, id));
            return SqlServerEditOutcomes.Affected(id, await command.ExecuteNonQueryAsync(cancellationToken));
        }
        catch (Exception exception) when (SqlServerEditOutcomes.IsFeatureFailure(exception))
        {
            return SqlServerEditOutcomes.FailureFor(id, exception);
        }
    }
}
