using System.Text.Json;
using Spatial.PluginSdk;

namespace Spatial.PluginSdk.Http;

// ---- rendering ----

/// <summary>
/// Primitive viewport shape for the wire (the known STJ struct-binding trap:
/// core value types never appear in HTTP DTOs).
/// </summary>
public sealed record ViewportDto(double MinX, double MinY, double MaxX, double MaxY, int Width, int Height, string Crs);

/// <summary>A styled layer's dataset, keyed store and optional store-level filter.</summary>
public sealed record RenderLayerDto(string Dataset, string? Store = null, string? Filter = null);

/// <summary>A configured imagery source to composite beneath the vector layers.</summary>
public sealed record RenderImageryDto(string Source, RasterBlend Blend = RasterBlend.Over, double Opacity = 1.0);

/// <summary>
/// The <c>POST /api/render</c> body. The style document is inline MapLibre
/// JSON; the host lowers it to the compiled draw plan.
/// </summary>
public sealed record RenderRequest(
    ViewportDto Viewport,
    JsonElement Style,
    IReadOnlyList<RenderLayerDto> Layers,
    IReadOnlyList<RenderImageryDto>? Imagery = null,
    RasterFormat Format = RasterFormat.Png,
    int Quality = 90,
    string? Background = null,
    bool Transparent = true,
    double Scale = 1.0);

/// <summary>A configured imagery source the host can read (path stays server-side).</summary>
public sealed record ImagerySourceDto(string Name);

/// <summary>The <c>GET /api/render/capabilities</c> body.</summary>
public sealed record RenderCapabilitiesResponse(
    IReadOnlyList<string> Formats,
    IReadOnlyList<string> PixelFormats,
    IReadOnlyList<string> BlendModes,
    long MaxPixels,
    IReadOnlyList<ImagerySourceDto> ImagerySources);
