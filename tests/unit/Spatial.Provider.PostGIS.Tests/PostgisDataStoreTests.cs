using Npgsql;
using Spatial.Provider.PostGIS.Data;

namespace Spatial.Provider.PostGIS.Tests;

/// <summary>
/// The offline-testable seams of the PostGIS data layer (ADR-0028): command
/// assembly binds parameters positionally (null becomes <c>DBNull</c>) over
/// an unopened connection, so no live database is required.
/// </summary>
public sealed class PostgisDataStoreTests
{
    [Fact]
    public void BuildCommand_binds_parameters_positionally_with_dbnull_for_null()
    {
        using var connection = new NpgsqlConnection("Host=unreachable.invalid;Username=spatial;Password=pw;Database=geodata");
        using var command = PostgisDataStore.BuildCommand(connection, "SELECT $1, $2", [1, null]);

        Assert.Equal("SELECT $1, $2", command.CommandText);
        Assert.Equal(2, command.Parameters.Count);
        Assert.Equal("p0", command.Parameters[0].ParameterName);
        Assert.Equal(1, command.Parameters[0].Value);
        Assert.Equal("p1", command.Parameters[1].ParameterName);
        Assert.Equal(DBNull.Value, command.Parameters[1].Value);
    }
}
