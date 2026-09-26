using Npgsql;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS;

/// <summary>
/// How one executed edit statement becomes one per-feature outcome
/// (ADR-0037): the insert path reads back the identity the database
/// assigned, the update and delete paths report the rows they affected, and
/// a store failure becomes a typed per-feature failure instead of aborting
/// the batch. Split out of <see cref="PostgisEditStore"/> so that class keeps
/// one responsibility — the batch/session/transaction lifecycle — and the
/// result shaping sits with the code that produces it.
/// </summary>
internal static class PostgisEditOutcomes
{
    /// <summary>An update: success when a row matched the feature, otherwise a typed not-found failure.</summary>
    public static async Task<FeatureEditOutcome> ApplyUpdateAsync(
        Feature feature, NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return Affected(feature.Id, affected);
    }

    /// <summary>An insert: reads back the assigned identity when the statement returns one.</summary>
    public static async Task<FeatureEditOutcome> ApplyInsertAsync(
        Feature feature, NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadInsertedIdentityAsync(feature, reader, cancellationToken);
    }

    /// <summary>The per-feature outcome of a statement: success, or a typed not-found failure.</summary>
    public static FeatureEditOutcome Affected(FeatureId id, int affected) =>
        affected > 0
            ? FeatureEditOutcome.Success(id)
            : FeatureEditOutcome.Failure(id, SpatialException.NotFound, $"No feature with identity '{id}' exists.");

    /// <summary>A no-row insert keeps the feature's own identity; a returned row carries the assigned one.</summary>
    private static async Task<FeatureEditOutcome> ReadInsertedIdentityAsync(
        Feature feature, NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken))
        {
            return FeatureEditOutcome.Success(feature.Id);
        }

        return ReadInsertedRow(feature, reader);
    }

    private static FeatureEditOutcome ReadInsertedRow(Feature feature, NpgsqlDataReader reader)
    {
        if (reader.FieldCount == 0)
        {
            return FeatureEditOutcome.Success(feature.Id);
        }

        var row = new object[reader.FieldCount];
        reader.GetValues(row);
        var indexes = Enumerable.Range(0, reader.FieldCount).ToArray();
        return FeatureEditOutcome.Success(new FeatureId(PostgisDiagnostics.FeatureIdentity(indexes, row, 0)));
    }

    /// <summary>The store failures that become a per-feature failure rather than an aborted batch.</summary>
    public static bool IsFeatureFailure(Exception exception) => exception is PostgresException or SpatialException;

    /// <summary>A store failure as a typed per-feature outcome, keeping the engine's own code.</summary>
    public static FeatureEditOutcome FailureFor(FeatureId id, Exception exception) => exception is PostgresException postgres
        ? FeatureEditOutcome.Failure(id, SpatialException.InvalidArguments, postgres.MessageText)
        : FeatureEditOutcome.Failure(id, ((SpatialException)exception).Code, ((SpatialException)exception).Message);
}
