using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.StoreConnections;

namespace Spatial.StoreConnections.Tests;

/// <summary>
/// The runtime connection registry, test-first: set/get/list/remove round
/// trips, blind saves, reload picks up on-disk edits, statuses never carry
/// secrets, misuse fails as invalid.arguments, a corrupt file as
/// store.unavailable, and cancellation is honoured.
/// </summary>
public sealed class StoreConnectionRegistryTests : IDisposable
{
    private const string Secret = "Host=db.local;Database=gis;Username=reader;Password=s3cret";

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-connections-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Path(string name = "connections.json") => System.IO.Path.Combine(_directory, name);

    private static StoreConnectionOptions Options(string path) => new() { Path = path };

    [Fact]
    public async Task Set_then_get_round_trips_with_status_metadata()
    {
        var registry = new FileStoreConnectionRegistry(Options(Path()));

        var status = await registry.SetAsync("postgis", Secret);

        Assert.Equal("postgis", status.Store);
        Assert.True(status.Configured);
        Assert.NotNull(status.UpdatedAt);
        Assert.True(registry.TryGetConnectionString("postgis", out var stored));
        Assert.Equal(Secret, stored);
    }

    [Fact]
    public async Task Store_names_normalise_to_lowercase()
    {
        var registry = new FileStoreConnectionRegistry(Options(Path()));

        await registry.SetAsync("PostGIS", Secret);

        Assert.True(registry.TryGetConnectionString("postgis", out _));
        var statuses = await registry.ListAsync();
        Assert.Equal("postgis", Assert.Single(statuses).Store);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("has-dash")]
    [InlineData("9lives")]
    [InlineData("schema.table")]
    public async Task Invalid_store_names_fail_as_invalid_arguments(string store)
    {
        var registry = new FileStoreConnectionRegistry(Options(Path()));

        var failure = await Assert.ThrowsAsync<SpatialException>(() => registry.SetAsync(store, Secret));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_connection_strings_fail_as_invalid_arguments(string connectionString)
    {
        var registry = new FileStoreConnectionRegistry(Options(Path()));

        var failure = await Assert.ThrowsAsync<SpatialException>(() => registry.SetAsync("postgis", connectionString));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.False(registry.TryGetConnectionString("postgis", out _));
    }

    [Fact]
    public async Task Remove_falls_back_to_no_entry()
    {
        var registry = new FileStoreConnectionRegistry(Options(Path()));

        Assert.False(await registry.RemoveAsync("postgis"));
        await registry.SetAsync("postgis", Secret);
        Assert.True(await registry.RemoveAsync("postgis"));
        Assert.False(registry.TryGetConnectionString("postgis", out _));
        Assert.Empty(await registry.ListAsync());
    }

    [Fact]
    public async Task Entries_survive_a_new_instance_over_the_same_file()
    {
        var path = Path();
        await new FileStoreConnectionRegistry(Options(path)).SetAsync("postgis", Secret);

        var reopened = new FileStoreConnectionRegistry(Options(path));

        Assert.True(reopened.TryGetConnectionString("postgis", out var stored));
        Assert.Equal(Secret, stored);
    }

    [Fact]
    public async Task Reload_picks_up_on_disk_edits()
    {
        var path = Path();
        var registry = new FileStoreConnectionRegistry(Options(path));
        await registry.SetAsync("postgis", Secret);

        await new FileStoreConnectionRegistry(Options(path)).SetAsync("postgis", "Host=other;Database=gis");
        var statuses = await registry.ReloadAsync();

        Assert.True(registry.TryGetConnectionString("postgis", out var stored));
        Assert.Equal("Host=other;Database=gis", stored);
        Assert.Equal("postgis", Assert.Single(statuses).Store);
    }

    [Fact]
    public async Task Reload_after_the_file_is_deleted_clears_entries()
    {
        var path = Path();
        var registry = new FileStoreConnectionRegistry(Options(path));
        await registry.SetAsync("postgis", Secret);

        File.Delete(path);
        await registry.ReloadAsync();

        Assert.False(registry.TryGetConnectionString("postgis", out _));
    }

    [Fact]
    public async Task Missing_file_means_no_entries()
    {
        var registry = new FileStoreConnectionRegistry(Options(Path("absent.json")));

        Assert.False(registry.TryGetConnectionString("postgis", out _));
        Assert.Empty(await registry.ListAsync());
    }

    [Fact]
    public async Task Corrupt_file_fails_as_store_unavailable()
    {
        var path = Path();
        await File.WriteAllTextAsync(path, "{not json");

        var registry = new FileStoreConnectionRegistry(Options(path));
        var failure = await Assert.ThrowsAsync<SpatialException>(() => registry.ReloadAsync());
        Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
    }

    [Fact]
    public async Task Statuses_never_carry_secrets()
    {
        var registry = new FileStoreConnectionRegistry(Options(Path()));
        await registry.SetAsync("postgis", Secret);

        foreach (var status in await registry.ListAsync())
        {
            Assert.DoesNotContain("s3cret", status.ToString(), StringComparison.Ordinal);
        }

        var reloaded = await registry.ReloadAsync();
        Assert.DoesNotContain("s3cret", reloaded.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelled_save_throws_without_touching_the_file()
    {
        var path = Path();
        var registry = new FileStoreConnectionRegistry(Options(path));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            registry.SetAsync("postgis", Secret, new CancellationToken(canceled: true)));

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Cancelled_reload_throws()
    {
        var registry = new FileStoreConnectionRegistry(Options(Path()));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            registry.ReloadAsync(new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Connection_file_is_owner_only_on_unix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var path = Path();
        var registry = new FileStoreConnectionRegistry(Options(path));
        await registry.SetAsync("postgis", Secret);

        var mode = File.GetUnixFileMode(path);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }
}
