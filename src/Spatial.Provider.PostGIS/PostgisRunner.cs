using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.Provider.PostGIS.Configuration;
using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Data;
using static Spatial.Provider.PostGIS.PostgisInvocationValidator;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// The invocation surface of the <c>postgis@1</c> provider (ADR-0028): a
/// capability dispatch table over the nine data-provider contracts. Each
/// handler runs the store-free validation guards of
/// <see cref="PostgisInvocationValidator"/> in contract order (dataset,
/// batch, bbox, filter, transaction reads; facilities; cancellation; the
/// no-connection-configuration guard) and, when every guard passes, delegates
/// the database work to <see cref="PostgisStoreExecutor"/> — so every handler
/// is one branch deep and the DB paths stay covered by the containerised
/// integration suite while the pure validation logic is covered by unit
/// tests. The advertised ids and descriptors come from
/// <see cref="PostgisCapabilities"/>.
/// </summary>
internal sealed class PostgisRunner
{
    private readonly PostgisConnectionConfiguration _configuration;
    private readonly PostgisStoreExecutor _executor;
    private readonly Dictionary<CapabilityId, Func<CapabilityInvocation, ValueTask<CapabilityResult>>> _handlers;

    public PostgisRunner(PostgisConnectionConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _executor = new PostgisStoreExecutor(
            _configuration,
            new Lazy<PostgisDataStore>(() => PostgisDataStore.Open(_configuration)));
        _handlers = new Dictionary<CapabilityId, Func<CapabilityInvocation, ValueTask<CapabilityResult>>>
        {
            [PostgisCapabilities.CatalogueList] = CatalogueListAsync,
            [PostgisCapabilities.DatasetDescribe] = DescribeAsync,
            [PostgisCapabilities.DatasetCreate] = CreateAsync,
            [PostgisCapabilities.FeatureScan] = ScanAsync,
            [PostgisCapabilities.FeatureQuery] = QueryAsync,
            [PostgisCapabilities.FeatureWrite] = WriteAsync,
            [PostgisCapabilities.TransactionBegin] = BeginAsync,
            [PostgisCapabilities.TransactionCommit] = CommitAsync,
            [PostgisCapabilities.TransactionRollback] = RollbackAsync,
        };
    }

    public static IReadOnlyList<CapabilityDescriptor> Descriptors => PostgisCapabilities.Descriptors;

    public ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return _handlers.TryGetValue(invocation.Capability, out var handler)
            ? handler(invocation)
            : new ValueTask<CapabilityResult>(CapabilityResult.Failure(NotServed(invocation)));
    }

    private static CapabilityError NotServed(CapabilityInvocation invocation) =>
        CapabilityError.ContractViolation($"{invocation.Capability} is not served by postgis@1.");

    // ---- Catalogue ----

    private async ValueTask<CapabilityResult> CatalogueListAsync(CapabilityInvocation invocation)
    {
        if (FirstError(
            ReadPattern(invocation, out var pattern),
            RequireFacilities(invocation, out var facilities),
            CheckCancelled(invocation),
            RequireConfigured(invocation)) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        return await ExecuteGuardedAsync(invocation, i => _executor.StartCatalogueStreamAsync(i, facilities, pattern));
    }

    // ---- Dataset describe + create ----

    private async ValueTask<CapabilityResult> DescribeAsync(CapabilityInvocation invocation)
    {
        if (FirstError(
            ReadDataset(invocation, out var dataset),
            RequireFacilities(invocation, out var facilities),
            CheckCancelled(invocation),
            RequireConfigured(invocation)) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        return await ExecuteGuardedAsync(invocation, i => _executor.ExecuteDescribeAsync(i, dataset, facilities));
    }

    private async ValueTask<CapabilityResult> CreateAsync(CapabilityInvocation invocation)
    {
        if (FirstError(
            ReadDataset(invocation, out var dataset),
            ReadSrid(invocation, out var srid),
            ReadBatchAndSchema(invocation, out var batch),
            CheckCancelled(invocation),
            RequireConfigured(invocation)) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        return await ExecuteGuardedAsync(invocation, i => _executor.ExecuteCreateAsync(i, dataset, batch, srid));
    }

    // ---- Feature scan + query ----

    private async ValueTask<CapabilityResult> ScanAsync(CapabilityInvocation invocation)
    {
        if (FirstError(
            ReadDataset(invocation, out var dataset),
            RequireFacilities(invocation, out var facilities),
            CheckCancelled(invocation),
            RequireConfigured(invocation)) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        return await ExecuteGuardedAsync(invocation, i => _executor.ExecuteScanAsync(i, dataset, facilities));
    }

    private async ValueTask<CapabilityResult> QueryAsync(CapabilityInvocation invocation)
    {
        if (FirstError(
            ReadDataset(invocation, out var dataset),
            ReadBoundingBox(invocation, out var boundingBox),
            TryReadFilter(invocation, out var filter),
            RequireFacilities(invocation, out var facilities),
            CheckCancelled(invocation),
            RequireConfigured(invocation)) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        return await ExecuteGuardedAsync(invocation, i => _executor.ExecuteQueryAsync(i, dataset, boundingBox, filter, facilities));
    }

    // ---- Feature write ----

    private async ValueTask<CapabilityResult> WriteAsync(CapabilityInvocation invocation)
    {
        if (FirstError(
            ReadDataset(invocation, out var dataset),
            ReadOptionalTransaction(invocation, out var transactionId),
            ReadBatch(invocation, out var batch),
            CheckCancelled(invocation),
            RequireConfigured(invocation)) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        return await ExecuteGuardedAsync(invocation, i => _executor.ExecuteWriteAsync(i, dataset, batch, transactionId));
    }

    // ---- Transactions ----

    private async ValueTask<CapabilityResult> BeginAsync(CapabilityInvocation invocation)
    {
        if (FirstError(
            RequireFacilities(invocation, out var facilities),
            CheckCancelled(invocation),
            RequireConfigured(invocation)) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        return await ExecuteGuardedAsync(invocation, i => _executor.ExecuteBeginAsync(i, facilities));
    }

    private async ValueTask<CapabilityResult> CommitAsync(CapabilityInvocation invocation) =>
        await EndTransactionAsync(invocation, commit: true);

    private async ValueTask<CapabilityResult> RollbackAsync(CapabilityInvocation invocation) =>
        await EndTransactionAsync(invocation, commit: false);

    private async ValueTask<CapabilityResult> EndTransactionAsync(CapabilityInvocation invocation, bool commit)
    {
        if (FirstError(
            ReadTransactionHandle(invocation, out var handle),
            CheckCancelled(invocation),
            RequireConfigured(invocation)) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        return await ExecuteGuardedAsync(invocation, i => _executor.ExecuteEndTransactionAsync(i, handle, commit));
    }


    // ---- The shared store-failure guard ----

    /// <summary>Delegates one store operation and maps any failure the same way every capability does.</summary>
    private async ValueTask<CapabilityResult> ExecuteGuardedAsync(
        CapabilityInvocation invocation,
        Func<CapabilityInvocation, ValueTask<CapabilityResult>> operation)
    {
        try
        {
            return await operation(invocation);
        }
        catch (Exception exception)
        {
            return CapabilityResult.Failure(_executor.MapFailure(invocation, exception));
        }
    }

    // ---- The no-connection-configuration guard ----

    /// <summary>The no-connection-configuration guard: valid work on an unconfigured provider is unavailable (redacted, actionable).</summary>
    private CapabilityError? RequireConfigured(CapabilityInvocation invocation) =>
        _configuration.IsConfigured
            ? null
            : PostgisDiagnostics.Unavailable(invocation.Capability);
}
