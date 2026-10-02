using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// The one collection the container-backed classes belong to. Its fixture is
/// <see cref="SqlServerContainerFixture"/>, so the container is started once
/// for the assembly rather than once per class — thirteen SQL Server
/// containers starting at once under a loaded lane is what degraded the suite
/// into a run that reported most of itself as skipped while saying nothing
/// about why (SpatialEngine-qhz, reported through ADR-0139).
/// </summary>
/// <remarks>
/// The collection does not run its classes in parallel: they share one SQL
/// Server instance, and each is given its own database
/// (<see cref="SqlServerDatabaseFixture"/>), so running them one at a time
/// costs the suite nothing it was not already paying in container starts.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlServerContainerDefinition : ICollectionFixture<SqlServerContainerFixture>
{
    public const string Name = "SQL Server container";
}

/// <summary>
/// The containerised SQL Server fixture (ADR-0073): one Testcontainers SQL
/// Server image, started once for the whole assembly with a generous budget
/// and more than one attempt. When the Docker daemon is not reachable — or the
/// host never gave the image the time it asked for — the fixture records an
/// explicit skip reason; every test checks it through <c>Skip.If</c> (never a
/// silent no-op). Test classes take a database inside it
/// (<see cref="SqlServerDatabaseFixture"/>) rather than a container of their
/// own.
/// </summary>
public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public bool DockerAvailable { get; private set; }

    public string? SkipReason { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var start = await SqlServerContainerStart.StartAsync(
            async cancellationToken =>
            {
                var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
                try
                {
                    await container.StartAsync(cancellationToken);
                }
                catch
                {
                    // The start owns what it created, so a retry is not a leak.
                    await container.DisposeAsync();
                    throw;
                }

                return container;
            },
            discardAsync: (container, _) => container.DisposeAsync().AsTask());

        DockerAvailable = start.Started;
        if (!DockerAvailable || start.Container is not MsSqlContainer container)
        {
            SkipReason = start.Reason ?? "the SQL Server container could not start.";
            return;
        }

        _container = container;
        ConnectionString = WithTrustServerCertificate(container.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// A connection string to a database of its own inside the shared
    /// container, created empty. Each test class takes one so that a class
    /// which writes to <c>dbo.places</c> is not fighting the class that
    /// asserts on it.
    /// </summary>
    public async Task<string> CreateDatabaseAsync(string database)
    {
        if (!IsSafeDatabaseName(database))
        {
            throw new ArgumentException(
                $"'{database}' is not a database name this fixture will create.",
                nameof(database));
        }

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{database}]";
        await command.ExecuteNonQueryAsync();

        return new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = database }.ConnectionString;
    }

    /// <summary>
    /// The container's connection string trusts its self-signed certificate on
    /// purpose: the fixture is a throwaway database, and a TLS handshake is not
    /// what these tests are about.
    /// </summary>
    public static string WithTrustServerCertificate(string connectionString) =>
        new SqlConnectionStringBuilder(connectionString)
        {
            TrustServerCertificate = true,
            Encrypt = false,
        }.ConnectionString;

    /// <summary>
    /// A database name the fixture will interpolate into <c>CREATE DATABASE</c>:
    /// a lowercase letter, then letters, digits and underscores. The names are
    /// generated here rather than taken from anywhere else, and this is the
    /// check that says so.
    /// </summary>
    private static bool IsSafeDatabaseName(string database) =>
        database.Length is > 0 and <= 64
        && database[0] is >= 'a' and <= 'z'
        && database.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');
}
