using Microsoft.Data.SqlClient;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// The fixture a container-backed test class takes: a database of its own
/// inside the one shared container (<see cref="SqlServerContainerFixture"/>),
/// seeded with the spatial fixtures the data-path tests assert against. A
/// class fixture rather than the container itself, because a container per
/// class is the contention this suite was reporting as skips
/// (SpatialEngine-qhz), and a database per class is what kept the classes from
/// writing over one another.
/// </summary>
public sealed class SqlServerDatabaseFixture : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _container;
    private string? _database;
    private string _connectionString = string.Empty;

    public SqlServerDatabaseFixture(SqlServerContainerFixture container) => _container = container;

    public bool DockerAvailable { get; private set; }

    public string? SkipReason { get; private set; }

    /// <summary>
    /// The connection string a container-backed fact reaches the store
    /// through. It refuses to answer while the container is not available
    /// (ADR-0193): a fact that forgot <c>Skip.If(!DockerAvailable, …)</c> then
    /// fails here with a message naming the guard, rather than opening a
    /// connection to an empty string and reporting a green run that tested
    /// nothing. The refusal is what makes the container-skip invariant
    /// structural; the source-reading backstop in
    /// <c>Spatial.Architecture.Tests</c> only audits it.
    /// </summary>
    public string ConnectionString
    {
        get
        {
            if (DockerAvailable)
            {
                return _connectionString;
            }

            var reason = SkipReason ?? "no reason recorded";
            throw new InvalidOperationException(
                $"This fact reached {nameof(SqlServerDatabaseFixture)}.{nameof(ConnectionString)} "
                + $"while {nameof(DockerAvailable)} was false, so it never guarded on the "
                + $"container. Call Skip.If(!_fixture.{nameof(DockerAvailable)}, "
                + $"_fixture.{nameof(SkipReason)} ?? \"no reason\") before the first access. "
                + $"Recorded reason: {reason}");
        }
        private set => _connectionString = value;
    }

    public async Task InitializeAsync()
    {
        if (!_container.DockerAvailable)
        {
            // The container's reason is this class's reason: the same absent
            // daemon, said once, in the words the container learned it in.
            SkipReason = _container.SkipReason;
            return;
        }

        _database = $"t{Guid.NewGuid():N}";

        try
        {
            ConnectionString = await _container.CreateDatabaseAsync(_database);
            DockerAvailable = true;
            await SeedAsync();
        }
        catch (Exception exception)
        {
            DockerAvailable = false;
            SkipReason = $"the SQL Server test database could not be prepared (is the Docker daemon reachable?): {exception.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (_database is null || !DockerAvailable)
        {
            return;
        }

        try
        {
            await using var connection = new SqlConnection(_container.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"IF DB_ID('{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END";
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception)
        {
            // The container is about to be disposed with the test run anyway:
            // a database this fixture failed to drop is not a test failure.
        }
    }

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
