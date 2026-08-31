using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Streams;

namespace Spatial.Provider.Demo;

/// <summary>
/// Groups demo features into canonical feature batches for the scan/query
/// streams (ADR-0020/0028): one <c>FeatureBatchCodec</c> v1 byte array per
/// batch in scan order, exactly like the PostGIS provider's stream rhythm —
/// clients cannot tell the demo data from database data. Owns the demo
/// feature stream end to end: the scan schema, the paging encode, and the
/// channel mint that carries the batches out of the handler.
/// </summary>
internal static class DemoFeatureBatches
{
    /// <summary>Features per stream item (canonical batch).</summary>
    public const int FeaturesPerBatch = 512;

    /// <summary>The scan schema: the first feature's schema (the demo sets are uniform).</summary>
    public static FeatureSchema ScanSchema(IEnumerable<Feature> features) =>
        features.FirstOrDefault()?.Schema ?? new FeatureSchema([]);

    /// <summary>
    /// Encodes all features as canonical batch bytes, one item per batch,
    /// and emits them on a fresh demo stream; the caller returns the handle.
    /// </summary>
    public static async ValueTask<CapabilityResult> EmitStreamAsync(
        ICapabilityFacilities facilities,
        IEnumerable<Feature> features,
        CancellationToken cancellationToken)
    {
        var channel = facilities.Streams.Create(DemoRunner.StreamKind, capacity: 4);
        _ = EmitAsync(channel, features, cancellationToken);
        return CapabilityResult.Success(channel.Handle);
    }

    private static async Task EmitAsync(
        StreamChannel channel, IEnumerable<Feature> features, CancellationToken cancellationToken)
    {
        foreach (var bytes in Encode(features))
        {
            await channel.Writer.WriteAsync(bytes, cancellationToken);
        }

        channel.Writer.Complete();
    }

    /// <summary>Encodes all features as canonical batch bytes, one item per batch.</summary>
    private static List<byte[]> Encode(IEnumerable<Feature> features)
    {
        var schema = ScanSchema(features);
        var items = new List<byte[]>();
        var page = new List<Feature>();
        foreach (var feature in features)
        {
            page.Add(feature);
            if (page.Count == FeaturesPerBatch)
            {
                items.Add(EncodeOne(schema, page));
                page = [];
            }
        }

        if (page.Count > 0)
        {
            items.Add(EncodeOne(schema, page));
        }

        return items;
    }

    private static byte[] EncodeOne(FeatureSchema schema, IReadOnlyList<Feature> features) =>
        FeatureBatchCodec.Encode(new FeatureBatch(schema, features));
}
