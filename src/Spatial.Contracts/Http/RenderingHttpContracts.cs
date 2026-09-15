using System.Text.Json;
using Spatial.Contracts;

namespace Spatial.Contracts.Http;

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

/// <summary>
/// The <c>POST /api/maps/{name}/render</c> body (ADR-0053 §4, evolving
/// ADR-0047): the same viewport and encoding inputs as
/// <see cref="RenderRequest"/> without a style or layer list, because both
/// come from the named map's persisted layers and their style fragments.
/// </summary>
public sealed record MapRenderRequestDto(
    ViewportDto Viewport,
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

// ---- tiles (R4) ----

/// <summary>A tile address on the wire.</summary>
public sealed record TileDto(int Z, int X, int Y);

/// <summary>
/// The <c>POST /api/render/tiles/{z}/{x}/{y}.{format}</c> body: the same
/// style/layer/encoding inputs as <see cref="RenderRequest"/> without a
/// viewport, because the viewport is derived from the tile address and the
/// scheme (<see cref="TileRenderRequest.Scheme"/>).
/// </summary>
public sealed record TileRenderRequest(
    JsonElement Style,
    IReadOnlyList<RenderLayerDto> Layers,
    IReadOnlyList<RenderImageryDto>? Imagery = null,
    string? Scheme = null,
    RasterFormat Format = RasterFormat.Png,
    int Quality = 90,
    string? Background = null,
    bool Transparent = true,
    double Scale = 1.0);

/// <summary>The <c>POST /api/render/tiles/batch</c> body: one request over an ordered tile list.</summary>
public sealed record TileBatchRequest(
    TileRenderRequest Request,
    IReadOnlyList<TileDto> Tiles);

/// <summary>One rendered tile of a batch: encoded bytes as Base64 plus its cache disposition.</summary>
public sealed record TileResultDto(
    int Z,
    int X,
    int Y,
    bool Cached,
    string ContentType,
    int Width,
    int Height,
    string Content);

/// <summary>The ordered result list of a tile batch, in request order.</summary>
public sealed record TileBatchResponse(IReadOnlyList<TileResultDto> Tiles);

/// <summary>One level of detail as served by <c>GET /api/render/tiles/capabilities</c>.</summary>
public sealed record TileLevelDto(int Zoom, double Resolution, double ScaleDenominator);

/// <summary>One registered tiling scheme as served by the tile capabilities route.</summary>
public sealed record TileSchemeDto(
    string Id,
    string Crs,
    int TileSize,
    int MinZoom,
    int MaxZoom,
    IReadOnlyList<TileLevelDto> Levels);

/// <summary>The <c>GET /api/render/tiles/capabilities</c> body.</summary>
public sealed record TileCapabilitiesResponse(
    string DefaultScheme,
    int MaxTilesPerBatch,
    IReadOnlyList<TileSchemeDto> Schemes);
