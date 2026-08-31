using Spatial.Core.Features;
using Spatial.Core.Features.Codec;

namespace Spatial.Provider.Demo;

/// <summary>
/// Groups demo features into canonical feature batches for the scan/query
/// streams (ADR-0020/0028): one <c>FeatureBatchCodec</c> v1 byte array per
/// batch in scan order, exactly like the PostGIS provider's stream rhythm —
/// clients cannot tell the demo data from database data.
/// </summary>
internal static class DemoFeatureBatches
{
    /// <summary>Features per stream item (canonical batch).</summary>
    public const int FeaturesPerBatch = 512;

    /// <summary>Encodes all features as canonical batch bytes, one item per batch.</summary>
    public static IReadOnlyList<byte[]> Encode(IFeatureSchema schema, IEnumerable<Feature> features)
    {
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

    private static byte[] EncodeOne(IFeatureSchema schema, IReadOnlyList<Feature> features) =>
        FeatureBatchCodec.Encode(new FeatureBatch(AsConcrete(schema), features));

    private static FeatureSchema AsConcrete(IFeatureSchema schema) =>
        (FeatureSchema)schema;
}
