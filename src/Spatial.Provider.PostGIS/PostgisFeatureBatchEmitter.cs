using Npgsql;
using Spatial.Core.Features;
using Spatial.PluginSdk.Streams;
using Spatial.Provider.PostGIS.Core;
using Spatial.Provider.PostGIS.Data;
using Spatial.Provider.PostGIS.Streams;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// The pure feature-batch emission loop shared by scan and query: reads rows,
/// maps them to features, batches them and flushes each full batch to the
/// channel, with a final partial-batch flush. No instance state, no store.
/// </summary>
internal static class PostgisFeatureBatchEmitter
{
    public static async Task EmitAsync(
        NpgsqlDataReader reader,
        IFeatureSchema schema,
        IReadOnlyList<int> identityIndexes,
        StreamChannel channel,
        CancellationToken token)
    {
        var features = new List<Feature>(FeatureBatchStream.FeaturesPerBatch);
        long ordinal = 0;
        while (await reader.ReadAsync(token))
        {
            var row = PostgisDataStore.ReadRow(reader, reader.FieldCount);
            await CollectAsync(features, schema, channel, PostgisRowMapper.MapRow(schema, identityIndexes, row, ordinal), token);
            ordinal++;
        }

        await FinalFlushAsync(features, schema, channel, token);
    }

    private static async Task CollectAsync(
        List<Feature> features,
        IFeatureSchema schema,
        StreamChannel channel,
        Feature feature,
        CancellationToken token)
    {
        features.Add(feature);
        if (features.Count < FeatureBatchStream.FeaturesPerBatch)
        {
            return;
        }

        await FlushAsync(features, schema, channel, token);
    }

    private static async Task FinalFlushAsync(List<Feature> features, IFeatureSchema schema, StreamChannel channel, CancellationToken token)
    {
        if (features.Count == 0)
        {
            return;
        }

        await FlushAsync(features, schema, channel, token);
    }

    private static async Task FlushAsync(List<Feature> features, IFeatureSchema schema, StreamChannel channel, CancellationToken token)
    {
        var bytes = FeatureBatchStream.Encode(schema, features);
        await channel.Writer.WriteAsync(bytes, token);
        features.Clear();
    }
}
