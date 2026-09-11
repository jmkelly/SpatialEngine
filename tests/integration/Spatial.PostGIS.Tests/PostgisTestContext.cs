using Npgsql;
using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Resources;
using Spatial.PluginSdk.Streams;
using Spatial.Provider.PostGIS;
using Spatial.Provider.PostGIS.Configuration;
using Spatial.Runtime.Capabilities;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The invocation context of the containerised PostGIS tests: a capability
/// runtime hosting an in-process <c>postgis@1</c> provider configured with
/// the container's connection string, plus stream-draining helpers
/// (canonical feature batches and the metadata JSON items).
/// </summary>
internal sealed class PostgisTestContext : IAsyncDisposable
{
    private readonly CapabilityRuntime _runtime;
    private readonly string _connectionString;

    private PostgisTestContext(CapabilityRuntime runtime, PostgisProvider provider, string connectionString)
    {
        _runtime = runtime;
        Provider = provider;
        _connectionString = connectionString;
    }

    public PostgisProvider Provider { get; }

    public static PostgisTestContext Create(string connectionString)
    {
        var registry = new CapabilityRegistry();
        var provider = new PostgisProvider(PostgisConnectionConfiguration.FromConnectionString(connectionString));
        registry.Register(provider);
        return new PostgisTestContext(new CapabilityRuntime(registry), provider, connectionString);
    }

    /// <summary>
    /// The permission set the data contracts require; the runtime permission
    /// gate rejects every data invocation without it, so the container tests
    /// grant the same set the conformance harness does.
    /// </summary>
    private static readonly HashSet<Permission> DataProviderPermissions =
    [
        Permission.Parse("spatial.feature.read"),
        Permission.Parse("spatial.feature.write"),
        Permission.Parse("spatial.dataset.create"),
    ];

    /// <summary>Invokes one capability through the runtime (data permissions granted).</summary>
    public Task<CapabilityOutcome> InvokeAsync(
        CapabilityId capability,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var invocation = CapabilityInvocation.Create(capability, arguments) with
        {
            GrantedPermissions = DataProviderPermissions,
            CancellationToken = cancellationToken,
        };
        return _runtime.InvokeAsync(invocation);
    }

    /// <summary>Invokes a scanning capability and decodes every streamed canonical feature batch.</summary>
    public async Task<List<FeatureBatch>> ReadBatchesAsync(
        CapabilityId capability,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var outcome = await InvokeAsync(capability, arguments, cancellationToken);
        Assert.True(outcome.TryGetValue(out var value), $"expected a stream handle, got {outcome.Error}");
        var handle = Assert.IsType<ResourceHandle>(value);

        Assert.True(_runtime.Resources.TryAcquireLease(handle, null, out var lease));
        Assert.True(_runtime.Resources.TryOpenStream(handle, lease, out var stream));
        try
        {
            var batches = new List<FeatureBatch>();
            while (true)
            {
                var items = await stream.ReadBatchAsync(16, cancellationToken);
                if (items.Count == 0)
                {
                    return batches;
                }

                foreach (var item in items)
                {
                    batches.Add(FeatureBatchCodec.Decode((byte[])item!));
                }
            }
        }
        finally
        {
            Assert.True(_runtime.Resources.ReleaseLease(lease));
            await _runtime.Resources.CloseAsync(handle);
        }
    }

    /// <summary>Invokes a metadata-streaming capability and drains every item.</summary>
    public async Task<List<object?>> ReadItemsAsync(
        CapabilityId capability,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var outcome = await InvokeAsync(capability, arguments, cancellationToken);
        Assert.True(outcome.TryGetValue(out var value), $"expected a stream handle, got {outcome.Error}");
        var handle = Assert.IsType<ResourceHandle>(value);

        Assert.True(_runtime.Resources.TryAcquireLease(handle, null, out var lease));
        Assert.True(_runtime.Resources.TryOpenStream(handle, lease, out var stream));
        try
        {
            var items = new List<object?>();
            while (true)
            {
                var batch = await stream.ReadBatchAsync(16, cancellationToken);
                if (batch.Count == 0)
                {
                    return items;
                }

                items.AddRange(batch);
            }
        }
        finally
        {
            Assert.True(_runtime.Resources.ReleaseLease(lease));
            await _runtime.Resources.CloseAsync(handle);
        }
    }

    /// <summary>Invokes a scanning capability and returns the open stream (mid-stream cancellation tests).</summary>
    public async Task<(ResourceHandle Handle, ICapabilityStream Stream)> InvokeScanStreamAsync(
        CapabilityId capability,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var outcome = await InvokeAsync(capability, arguments, cancellationToken);
        Assert.True(outcome.TryGetValue(out var value), $"expected a stream handle, got {outcome.Error}");
        var handle = Assert.IsType<ResourceHandle>(value);
        Assert.True(_runtime.Resources.TryAcquireLease(handle, null, out var lease));
        Assert.True(_runtime.Resources.TryOpenStream(handle, lease, out var stream));
        return (handle, stream);
    }

    /// <summary>Counts rows through a raw SQL statement (the source of truth for write/transaction effects).</summary>
    public async Task<long> CountAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
