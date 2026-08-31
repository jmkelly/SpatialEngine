namespace Spatial.Core.Geometry.Codec;

/// <summary>
/// Thrown when binary data is not a valid canonical geometry (see
/// <see cref="GeometryCodec"/>). Messages carry the byte offset of the
/// problem.
/// </summary>
public sealed class CanonicalFormatException : FormatException
{
    public CanonicalFormatException(string message)
        : base(message)
    {
    }
}
