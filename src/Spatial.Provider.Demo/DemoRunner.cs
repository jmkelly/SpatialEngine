using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.PluginSdk.Resources;
using Spatial.PluginSdk.Streams;
using static Spatial.Provider.Demo.DemoInvocationValidator;

namespace Spatial.Provider.Demo;

/// <summary>
/// The invocation surface of the <c>demo@1</c> provider (Phase 10,
/// ADR-0031): a capability dispatch table over the read-only data contracts
/// and the demo sleep. Every handler runs the store-free validation guards
/// in contract order and, when they pass, streams its answer from the
/// in-memory catalog through the invocation facilities — one branch deep,
/// mirroring the PostGIS runner's shape. Dataset metadata crosses as JSON
/// text items, feature data as canonical batch bytes (ADR-0020/0028).
/// </summary>
internal sealed class DemoRunner
{
    /// <summary>The resource kind of every demo stream the provider mints.</summary>
    private static readonly ResourceKind StreamKind = ResourceKind.Parse("demo.stream");

    private readonly Dictionary<CapabilityId, Func<CapabilityInvocation, ValueTask<CapabilityResult>>> _handlers;

    public DemoRunner()
    {
        _handlers = new Dictionary<CapabilityId, Func<CapabilityInvocation, ValueTask<CapabilityResult>>>
        {
            [DemoCapabilities.CatalogueList] = CatalogueListAsync,
            [DemoCapabilities.DatasetDescribe] = DescribeAsync,
            [DemoCapabilities.FeatureScan] = ScanAsync,
            [DemoCapabilities.FeatureQuery] = QueryAsync,
            [DemoCapabilities.Sleep] = DemoSleep.SleepForAsync,
        };
    }

    public static IReadOnlyList<CapabilityDescriptor> Descriptors => DemoCapabilities.Descriptors;

    public ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return _handlers.TryGetValue(invocation.Capability, out var handler)
            ? handler(invocation)
            : new ValueTask<CapabilityResult>(CapabilityResult.Failure(
                CapabilityError.ContractViolation($"{invocation.Capability} is not served by demo@1.")));
    }

    // ---- Catalogue ----

    private async ValueTask<CapabilityResult> CatalogueListAsync(CapabilityInvocation invocation)
    {
        if (FirstError(
            ReadPattern(invocation, out var pattern),
            RequireFacilities(invocation, out var facilities),
            CheckCancelled(invocation)) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        var channel = facilities.Streams.Create(StreamKind, capacity: 4);
        _ = EmitCatalogueAsync(channel, pattern, invocation.CancellationToken);
        return CapabilityResult.Success(channel.Handle);
    }

    // ---- Dataset describe ----

    private async ValueTask<CapabilityResult> DescribeAsync(CapabilityInvocation invocation)
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

        var channel = facilities.Streams.Create(StreamKind, capacity: 2);
        _ = EmitDescribeAsync(channel, found, invocation.CancellationToken);
        return CapabilityResult.Success(channel.Handle);
    }

    // ---- Feature scan + query ----

    private async ValueTask<CapabilityResult> ScanAsync(CapabilityInvocation invocation)
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

        return await StreamFeatureBatchesAsync(invocation, facilities, found.Features);
    }

    private async ValueTask<CapabilityResult> QueryAsync(CapabilityInvocation invocation)
    {
        if (FirstError(
            ReadDataset(invocation, out var dataset),
            RejectFilter(invocation),
            ReadBoundingBox(invocation, out var minX, out var minY, out var maxX, out var maxY),
            RequireFacilities(invocation, out var facilities),
            CheckCancelled(invocation)) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        if (DemoDatasetCatalog.Find(dataset) is not { } found)
        {
            return CapabilityResult.Failure(UnknownDataset(invocation, dataset));
        }

        var hasBox = invocation.Arguments.ContainsKey("minx");
        var features = hasBox ? found.Features.Where(feature => Intersects(feature, minX, minY, maxX, maxY)) : found.Features;
        return await StreamFeatureBatchesAsync(invocation, facilities, features);
    }

    // ---- Emitters ----

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

    private static async ValueTask<CapabilityResult> StreamFeatureBatchesAsync(
        CapabilityInvocation invocation,
        ICapabilityFacilities facilities,
        IEnumerable<Feature> features)
    {
        var channel = facilities.Streams.Create(StreamKind, capacity: 4);
        _ = EmitBatchesAsync(channel, features, invocation.CancellationToken);
        return CapabilityResult.Success(channel.Handle);
    }

    private static async Task EmitBatchesAsync(
        StreamChannel channel, IEnumerable<Feature> features, CancellationToken cancellationToken)
    {
        foreach (var bytes in DemoFeatureBatches.Encode(ScanSchema(features), features))
        {
            await channel.Writer.WriteAsync(bytes, cancellationToken);
        }

        channel.Writer.Complete();
    }

    // ---- Pure helpers ----

    /// <summary>The scan schema: the first feature's schema (the demo sets are uniform).</summary>
    private static FeatureSchema ScanSchema(IEnumerable<Feature> features) =>
        features.FirstOrDefault()?.Schema ?? new FeatureSchema([]);

    private static CapabilityError UnknownDataset(CapabilityInvocation invocation, string dataset)
    {
        var available = string.Join(", ", DemoDatasetCatalog.Datasets.Select(item => $"'{item.Id}'"));
        return CapabilityError.InvalidArguments(
            $"{invocation.Capability}: no dataset '{dataset}' exists in the demo catalog. Available: {available}.");
    }

    /// <summary>LIKE-style pattern matching: '%' matches any run, '_' matches one character.</summary>
    private static bool Matches(string text, string? pattern)
    {
        if (pattern is null)
        {
            return true;
        }

        return MatchFrom(text, pattern, 0, 0);

        static bool MatchFrom(string text, string pattern, int textIndex, int patternIndex)
        {
            while (patternIndex < pattern.Length)
            {
                var token = pattern[patternIndex];
                if (token == '%')
                {
                    for (var next = textIndex; next <= text.Length; next++)
                    {
                        if (MatchFrom(text, pattern, next, patternIndex + 1))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                if (textIndex >= text.Length)
                {
                    return false;
                }

                if (token != '_' && token != text[textIndex])
                {
                    return false;
                }

                textIndex++;
                patternIndex++;
            }

            return textIndex == text.Length;
        }
    }

    /// <summary>Whether a feature's geometry envelope intersects the query box.</summary>
    private static bool Intersects(Feature feature, double minX, double minY, double maxX, double maxY)
    {
        var envelope = GeometryEnvelope(feature);
        return envelope is { } box
            && box.MinX <= maxX && box.MaxX >= minX
            && box.MinY <= maxY && box.MaxY >= minY;
    }

    private static Envelope? GeometryEnvelope(Feature feature)
    {
        for (var i = 0; i < feature.Schema.Count; i++)
        {
            var value = feature[i];
            if (value.Kind == AttributeKind.Geometry && value.GeometryValue is { IsEmpty: false } geometry)
            {
                return geometry.Envelope;
            }
        }

        return null;
    }
}
