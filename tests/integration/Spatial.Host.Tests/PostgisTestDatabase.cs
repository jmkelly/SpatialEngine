using Npgsql;
using Testcontainers.PostgreSql;

namespace Spatial.Host.Tests;

/// <summary>
/// The one PostGIS container the host integration suite shares
/// (ADR-0028, ADR-0033): started at most once per test process and reached
/// through <see cref="ConnectionString"/>, so no operator sets
/// <c>SPATIAL_POSTGIS_CONNECTION</c> to run the suite. When the Docker
/// daemon is not reachable the container is absent,
/// <see cref="SkipReason"/> explains why, and every test that needs the
/// database skips through <c>Skip.If</c> — never a silent no-op.
/// </summary>
/// <remarks>
/// The container is not disposed explicitly: Testcontainers' reaper (Ryuk)
/// removes it when the test process exits. The image is the one the
/// provider suite pins, so both suites share a single local image.
/// </remarks>
internal static class PostgisTestDatabase
{
    /// <summary>The image under test: PostGIS 3.4 on PostgreSQL 16.</summary>
    public const string Image = "postgis/postgis:16-3.4";

    private static readonly Lazy<Container?> Lazy = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether the container is running.</summary>
    public static bool Available => Lazy.Value?.Failure is null;

    /// <summary>Why the container is absent, or <c>null</c> when it is running.</summary>
    public static string? SkipReason => Lazy.Value?.Failure;

    /// <summary>The container's connection string, or <c>null</c> when it is absent.</summary>
    public static string? ConnectionString => Lazy.Value?.ConnectionString;

    /// <summary>Runs SQL against the container database (fixture setup and teardown).</summary>
    public static async Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        var connectionString = ConnectionString ?? throw new InvalidOperationException(
            $"the PostGIS container is not running, so SQL cannot run: {SkipReason}");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Container? Start()
    {
        var container = new PostgreSqlBuilder(Image)
            .WithDatabase("spatial")
            .WithUsername("spatial")
            .WithPassword("spatial")
            .Build();

        try
        {
            container.StartAsync().GetAwaiter().GetResult();
            return new Container(container.GetConnectionString());
        }
        catch (Exception exception)
        {
            return new Container(
                Failure: $"the PostGIS container could not start (is the Docker daemon reachable?): {exception.Message}");
        }
    }

    /// <summary>Either the running container's connection string or why it is absent — never both.</summary>
    private sealed record Container(string? ConnectionString = null, string? Failure = null);
}
