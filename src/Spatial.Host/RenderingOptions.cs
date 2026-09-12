namespace Spatial.Host.Api;

/// <summary>
/// Host configuration for the raster pipeline (<c>Spatial:Rendering</c>),
/// ADR-0044. The advertised formats gate what a request may ask for; the
/// pixel and layer caps bound a single request.
/// </summary>
internal sealed class RenderingOptions
{
    public IReadOnlyList<string> Formats { get; set; } = ["png", "jpeg", "webp"];

    public long MaxPixels { get; set; } = 16_777_216;

    public int MaxLayers { get; set; } = 32;
}

/// <summary>
/// Configured imagery sources (<c>Spatial:Imagery</c>): named filesystem
/// paths, never caller-supplied URLs (ADR-0044 SSRF rule).
/// </summary>
internal sealed class ImageryOptions
{
    public IReadOnlyList<ImagerySource> Sources { get; set; } = [];

    internal sealed class ImagerySource
    {
        public string Name { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;
    }

    /// <summary>Builds the source-name-to-path map the imagery service resolves against.</summary>
    public IReadOnlyDictionary<string, string> ToSourceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in Sources)
        {
            if (!string.IsNullOrWhiteSpace(source.Name) && !string.IsNullOrWhiteSpace(source.Path))
            {
                map[source.Name] = source.Path;
            }
        }

        return map;
    }
}
