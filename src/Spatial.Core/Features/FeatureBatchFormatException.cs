namespace Spatial.Core.Features;

/// <summary>
/// Thrown when binary data is not a valid feature batch (see
/// <see cref="FeatureBatchCodec"/>). Messages carry the byte offset of the
/// problem.
/// </summary>
public sealed class FeatureBatchFormatException : FormatException
{
    public FeatureBatchFormatException(string message)
        : base(message)
    {
    }
}
