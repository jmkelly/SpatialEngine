using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.PluginSdk.Streams;
using static Spatial.Provider.Demo.DemoInvocationValidator;

namespace Spatial.Provider.Demo;

/// <summary>
/// The catalogue-metadata handlers of <c>demo@1</c>: dataset listing and
/// single-dataset description, streamed as JSON text items from the
/// in-memory catalogue. Every handler runs the store-free validation guards
/// in contract order (see <see cref="DemoInvocationValidator"/>) and, when
/// they pass, streams its answer — one metadata-item shape, split from the
/// runner so the dispatch surface stays thin.
/// </summary>
internal static class DemoCatalogueHandler
{
    /// <summary>Streams the catalogue ids (optionally pattern-filtered).</summary>
    public static async ValueTask<CapabilityResult> CatalogueListAsync(CapabilityInvocation invocation)
    {
        if (FirstError(
            ReadPattern(invocation, out var pattern),
            RequireFacilities(invocation, out var facilities),
            CheckCancelled(invocation)) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        var channel = facilities.Streams.Create(DemoRunner.StreamKind, capacity: 4);
        _ = EmitCatalogueAsync(channel, pattern, invocation.CancellationToken);
        return CapabilityResult.Success(channel.Handle);
    }

    /// <summary>Streams one dataset's description.</summary>
    public static async ValueTask<CapabilityResult> DescribeAsync(CapabilityInvocation invocation)
    {
        if (FirstError(
            ReadDataset(invocation, out var dataset),
            RequireFacilities(invocation, out var facilities),
            CheckCancelled(invocation)) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        if (DemoDatasetCatalog.Find(dataset) is not { } found)
        {
            return CapabilityResult.Failure(UnknownDataset(invocation, dataset));
        }

        var channel = facilities.Streams.Create(DemoRunner.StreamKind, capacity: 2);
        _ = EmitDescribeAsync(channel, found, invocation.CancellationToken);
        return CapabilityResult.Success(channel.Handle);
    }

    private static async Task EmitCatalogueAsync(
        StreamChannel channel, string? pattern, CancellationToken cancellationToken)
    {
        foreach (var dataset in DemoDatasetCatalog.Datasets.Where(dataset => Matches(dataset.Id, pattern)))
        {
            await channel.Writer.WriteAsync(
                DatasetMetadataJson.WriteSummary(dataset.ToSummary()), cancellationToken);
        }

        channel.Writer.Complete();
    }

    private static async Task EmitDescribeAsync(
        StreamChannel channel, DemoDataset dataset, CancellationToken cancellationToken)
    {
        await channel.Writer.WriteAsync(
            DatasetMetadataJson.WriteDescription(dataset.ToDescription()), cancellationToken);
        channel.Writer.Complete();
    }

    /// <summary>
    /// LIKE-style pattern matching over dataset ids; see <see cref="LikePattern"/>.
    /// </summary>
    private static bool Matches(string text, string? pattern) => LikePattern.Matches(text, pattern);
}
