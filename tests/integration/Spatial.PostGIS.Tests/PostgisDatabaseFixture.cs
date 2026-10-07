using Npgsql;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The fixture a container-backed test class takes: a database of its own
/// inside the one shared container (<see cref="PostgisContainerFixture"/>),
/// seeded with the spatial fixtures the data-path tests assert against. A
/// class fixture rather than the container itself, because a container per
/// class is the contention this suite was reporting as skips
/// (SpatialEngine-o5p), and a database per class is what kept the classes
/// from writing over one another.
/// </summary>
public sealed class PostgisDatabaseFixture : IAsyncLifetime
{
    private readonly PostgisContainerFixture _container;
    private string? _database;
    private string _connectionString = string.Empty;

    public PostgisDatabaseFixture(PostgisContainerFixture container) => _container = container;

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
                $"This fact reached {nameof(PostgisDatabaseFixture)}.{nameof(ConnectionString)} "
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
            SkipReason = $"the PostGIS test database could not be prepared (is the Docker daemon reachable?): {exception.Message}";
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
            await using var dataSource = NpgsqlDataSource.Create(_container.AdministrativeConnectionString());
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            // FORCE (PostgreSQL 13 and later) is what lets the drop go through
            // while a connection into the database is still open — a pool that
            // has not been collected would otherwise hold this fixture's own
            // seeding connection and hang the teardown.
            command.CommandText = $"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)";
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception)
        {
            // The container is about to be disposed with the test run anyway: a
            // database this fixture failed to drop is not a test failure.
        }
    }

    /// <summary>Seeds the fixture tables (or is a no-op when the fixture was skipped).</summary>
    private async Task SeedAsync()
    {
        if (!DockerAvailable)
        {
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE EXTENSION IF NOT EXISTS postgis;

            CREATE TABLE places (
                id bigint PRIMARY KEY,
                name text NOT NULL,
                geom geometry(Point, 4326) NOT NULL
            );
            INSERT INTO places (id, name, geom) VALUES
                (1, 'Berlin', ST_GeomFromText('POINT(13.405 52.5200)', 4326)),
                (2, 'London', ST_GeomFromText('POINT(-0.1276 51.5072)', 4326)),
                (3, 'Paris',  ST_GeomFromText('POINT(2.3522 48.8566)', 4326));

            CREATE TABLE roads (
                id bigint,
                kind text,
                geom geometry(LineString, 3857)
            );
            INSERT INTO roads (id, kind, geom) VALUES
                (1, 'highway', ST_GeomFromText('LINESTRING(0 0, 10 10)', 3857)),
                (2, 'trail',   ST_GeomFromText('LINESTRING(5 5, 20 20)', 3857));

            CREATE TABLE bigpoints (
                id bigint NOT NULL,
                geom geometry(Point, 4326) NOT NULL
            );
            INSERT INTO bigpoints (id, geom)
            SELECT g, ST_MakePoint((g % 1000)::double precision / 10, (g / 1000)::double precision / 10)
            FROM generate_series(1, 200000) AS g;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
