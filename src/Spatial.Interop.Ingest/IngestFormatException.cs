namespace Spatial.Interop.Ingest;

/// <summary>
/// A typed failure of an ingest codec: a malformed document, an unsupported
/// geometry, a missing column or an invalid decode option. The host and the
/// Esri admin projection translate it into the engine's
/// <c>invalid.arguments</c>; it never escapes as a foreign format concept.
/// </summary>
public sealed class IngestFormatException : FormatException
{
    public IngestFormatException(string message)
        : base(message)
    {
    }

    public IngestFormatException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
