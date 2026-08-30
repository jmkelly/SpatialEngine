using Npgsql;
using Testcontainers.PostgreSql;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The containerised PostGIS fixture (ADR-0010/0028): a Testcontainers
/// PostGIS image started once per test class. When the Docker daemon is not
/// reachable the fixture records an explicit skip reason; every test checks
/// it through <c>Skip.If</c> (never a silent no-op). The fixture also seeds
/// the spatial fixtures the data-path tests assert against.
/// </summary>
public sealed class PostgisContainerFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public bool DockerAvailable { get; private set; }

    public string? SkipReason { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgis/postgis:16-3.4")
                .WithDatabase("spatial")
                .WithUsername("spatial")
                .WithPassword("spatial")
                .Build();
            await _container.StartAsync();
            DockerAvailable = true;
            ConnectionString = _container.GetConnectionString();
            await SeedAsync();
        }
        catch (Exception exception)
        {
            DockerAvailable = false;
            SkipReason = $"the PostGIS container could not start (is the Docker daemon reachable?): {exception.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
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
