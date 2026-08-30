namespace Spatial.PluginHost.DotNet.Manifest;

/// <summary>
/// Thrown when a plugin manifest is missing, malformed or violates a schema
/// rule. The message names every problem (actionable diagnostics, plan §22).
/// </summary>
public sealed class PluginManifestException : Exception
{
    public PluginManifestException(string message)
        : base(message)
    {
    }

    public PluginManifestException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
