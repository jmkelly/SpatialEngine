namespace Spatial.Rendering.Skia;

/// <summary>
/// Resource caps for a render request (ADR-0044 §security): pixel count and
/// layer count. Configured by the host; defaults are deliberately generous
/// for a single export but far below a denial-of-service frame.
/// </summary>
public sealed record RenderLimits(long MaxPixels = 16_777_216, int MaxLayers = 32)
{
    public static RenderLimits Default { get; } = new();
}
