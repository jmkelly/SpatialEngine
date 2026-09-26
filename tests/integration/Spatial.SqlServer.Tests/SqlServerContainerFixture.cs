using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// The containerised SQL Server fixture (ADR-0073): a Testcontainers SQL
/// Server image started once per test class. When the Docker daemon is not
/// reachable the fixture records an explicit skip reason; every test checks it
/// through <c>Skip.If</c> (never a silent no-op). The fixture also seeds the
/// spatial fixtures the data-path tests assert against.
/// </summary>
public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    public bool DockerAvailable { get; private set; }

    public string? SkipReason { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync();
            DockerAvailable = true;
            ConnectionString = WithTrustServerCertificate(_container.GetConnectionString());
            await SeedAsync();
        }
        catch (Exception exception)
        {
            DockerAvailable = false;
            SkipReason = $"the SQL Server container could not start (is the Docker daemon reachable?): {exception.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
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

    /// <summary>Seeds the fixture tables (or is a no-op when the fixture was skipped).</summary>
    private async Task SeedAsync()
    {
        if (!DockerAvailable)
        {
            return;
        }

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE places (
                id int NOT NULL PRIMARY KEY,
                name nvarchar(100) NOT NULL,
                geom geometry NOT NULL
            );
            INSERT INTO places (id, name, geom) VALUES
                (1, N'Berlin', geometry::STGeomFromText('POINT(13.405 52.5200)', 4326)),
                (2, N'London', geometry::STGeomFromText('POINT(-0.1276 51.5072)', 4326)),
                (3, N'Paris',  geometry::STGeomFromText('POINT(2.3522 48.8566)', 4326));

            CREATE TABLE roads (
                id int NOT NULL,
                kind nvarchar(50) NULL,
                geom geometry NOT NULL
            );
            INSERT INTO roads (id, kind, geom) VALUES
                (1, N'highway', geometry::STGeomFromText('LINESTRING(0 0, 10 10)', 3857)),
                (2, N'trail',   geometry::STGeomFromText('LINESTRING(5 5, 20 20)', 3857));

            CREATE TABLE bigpoints (
                id int NOT NULL,
                geom geometry NOT NULL
            );
            INSERT INTO bigpoints (id, geom)
            SELECT TOP (200000) g, geometry::STGeomFromText('POINT(' + CAST((g % 1000) AS varchar(20)) + ' ' + CAST((g / 1000) AS varchar(20)) + ')', 4326)
            FROM (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS g FROM sys.all_objects) AS numbers;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
