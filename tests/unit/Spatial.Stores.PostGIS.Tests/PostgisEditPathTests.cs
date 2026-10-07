using System.Data;
using Npgsql;
using Spatial.Contracts;
using Spatial.Core.Features;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// The edit path's container-free leaves (ADR-0037): the session's command
/// builder, which binds the planner's values positionally on the session's
/// connection and transaction, and the outcome shaping that turns an executed
/// statement into a per-feature success or a typed failure.
///
/// <para>
/// An <see cref="NpgsqlCommand"/> can be built without opening anything, so
/// the binding rule — a value is a named parameter, a null is
/// <see cref="DBNull"/>, and the statement is the one the planner wrote — is
/// measured here rather than only through a live edit, which is the half that
/// skips first on a loaded box (ADR-0187, ADR-0189).
/// </para>
/// </summary>
public sealed class PostgisEditPathTests
{
    private static NpgsqlConnection Connection() =>
        new("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1");

    [Fact]
    public async Task The_session_command_binds_the_values_positionally()
    {
        var connection = Connection();
        try
        {
            var session = new PostgisEditSession(connection, transaction: null, ownsConnection: false);

            await using var command = session.CreateCommand(
                "UPDATE \"public\".\"places\" SET \"name\" = @p0 WHERE \"id\" = @p1", ["Alpha", null]);

            Assert.Same(connection, session.Connection);
            Assert.Equal("UPDATE \"public\".\"places\" SET \"name\" = @p0 WHERE \"id\" = @p1", command.CommandText);
            Assert.Null(command.Transaction);
            Assert.Equal(["p0", "p1"], command.Parameters.Select(parameter => parameter.ParameterName).ToArray());
            Assert.Equal("Alpha", command.Parameters[0].Value);
            Assert.Equal(DBNull.Value, command.Parameters[1].Value);
        }
        finally
        {
            connection.Dispose();
        }
    }

    /// <summary>
    /// A session over a connection the caller owns leaves that connection
    /// alone when it is disposed: the caller's connection outlives the edit
    /// session (ADR-0037).
    /// </summary>
    [Fact]
    public async Task A_borrowed_connection_is_not_disposed_with_the_session()
    {
        await using var connection = Connection();
        var session = new PostgisEditSession(connection, transaction: null, ownsConnection: false);

        await session.DisposeAsync();

        // The connection is still usable: building a command on it does not
        // throw, which it would if the session had disposed it.
        await using var command = connection.CreateCommand();
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    /// <summary>A statement that changed a row is a success named by the feature's own identity.</summary>
    [Fact]
    public void An_affected_row_is_a_success_named_by_the_feature_identity()
    {
        var id = new FeatureId("7");

        var outcome = PostgisEditOutcomes.Affected(id, affected: 1);

        Assert.True(outcome.Succeeded);
        Assert.Equal(id, outcome.Id);
        Assert.Null(outcome.ErrorCode);
    }

    /// <summary>A statement that matched no row is the contract's typed <c>not.found</c>.</summary>
    [Fact]
    public void A_missing_row_is_a_typed_not_found()
    {
        var id = new FeatureId("7");

        var outcome = PostgisEditOutcomes.Affected(id, affected: 0);

        Assert.False(outcome.Succeeded);
        Assert.Equal(id, outcome.Id);
        Assert.Equal(SpatialException.NotFound, outcome.ErrorCode);
        Assert.Contains("7", outcome.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>Only a database- or engine-level failure becomes a per-feature failure; anything else aborts the batch.</summary>
    [Fact]
    public void Only_a_postgres_or_spatial_failure_is_a_feature_failure()
    {
        Assert.True(PostgisEditOutcomes.IsFeatureFailure(new PostgresException("boom", "ERROR", "ERROR", "23505")));
        Assert.True(PostgisEditOutcomes.IsFeatureFailure(SpatialException.BadArguments("bad")));
        Assert.False(PostgisEditOutcomes.IsFeatureFailure(new InvalidOperationException("bug")));
    }

    /// <summary>
    /// A database failure is attributed to the feature as <c>invalid.arguments</c>
    /// with the server's own message, and an engine failure keeps its own code
    /// and message (ADR-0037).
    /// </summary>
    [Fact]
    public void A_feature_failure_keeps_the_engine_code_and_maps_a_postgres_failure()
    {
        var id = new FeatureId("7");

        var postgres = PostgisEditOutcomes.FailureFor(id, new PostgresException("duplicate key", "ERROR", "ERROR", "23505"));
        Assert.Equal(SpatialException.InvalidArguments, postgres.ErrorCode);
        Assert.Equal("duplicate key", postgres.ErrorMessage);

        var spatial = PostgisEditOutcomes.FailureFor(id, SpatialException.BadArguments("no primary key"));
        Assert.Equal(SpatialException.InvalidArguments, spatial.ErrorCode);
        Assert.Equal("no primary key", spatial.ErrorMessage);
    }
}
