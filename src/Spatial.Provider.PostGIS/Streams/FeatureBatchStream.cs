using Spatial.Core.Features;

namespace Spatial.Provider.PostGIS.Streams;

/// <summary>
/// Groups features into canonical feature batches (ADR-0020/0028): a scan or
/// query emits one <c>FeatureBatchCodec</c> v1 byte array per
/// <see cref="FeaturesPerBatch"/> features onto the bounded stream (one item
/// per batch, in scan order), and writes/creates decode their byte-array
/// batch arguments through the same codec. Encoding is core behaviour
/// (<c>FeatureBatchCodec</c>); this helper controls only the batching rhythm
/// and the stream-vs-bytes shapes.
/// </summary>
internal static class FeatureBatchStream
{
    /// <summary>Features per stream item (canonical batch).</summary>
    public const int FeaturesPerBatch = 512;

    /// <summary>The bounded stream's buffer capacity in items (batches).</summary>
    public const int StreamCapacity = 8;

    /// <summary>Encodes a full batch of features to canonical bytes.</summary>
    public static byte[] Encode(FeatureSchema schema, IReadOnlyList<Feature> features) =>
        FeatureBatchCodec.Encode(new FeatureBatch(schema, features));

    /// <summary>Decodes a canonical batch argument; false with an actionable reason when malformed.</summary>
    public static bool TryDecode(byte[] bytes, out FeatureBatch batch, out string error)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (FeatureBatchCodec.TryDecode(bytes, out var decoded, out string? decodeError))
        {
            batch = decoded!;
            error = string.Empty;
            return true;
        }

        batch = null!;
        error = decodeError ?? "the batch is malformed";
        return false;
    }
}
