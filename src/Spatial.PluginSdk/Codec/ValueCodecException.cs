namespace Spatial.PluginSdk.Codec;

/// <summary>A value that cannot cross a public boundary in the inline codec (ADR-0020/0025/0030).</summary>
public class ValueCodecException : Exception
{
    public ValueCodecException(string message)
        : base(message)
    {
    }

    public ValueCodecException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
