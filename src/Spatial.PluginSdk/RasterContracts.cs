using Spatial.Core.Geometry;

namespace Spatial.PluginSdk;

/// <summary>The container formats the raster pipeline can encode (ADR-0044).</summary>
public enum RasterFormat
{
    Png,
    Jpeg,
    Webp,
    Tiff,
}

/// <summary>The channel layout of a raw pixel buffer.</summary>
public enum RasterPixelFormat
{
    /// <summary>Four channels, red-green-blue-alpha, 8 bits per channel.</summary>
    Rgba8888,

    /// <summary>Three channels, red-green-blue, 8 bits per channel.</summary>
    Rgb888,
}

/// <summary>The blend function used to composite one raster layer over the stack below it.</summary>
public enum RasterBlend
{
    Over,
    Multiply,
    Screen,
    Darken,
    Lighten,
}

/// <summary>
/// The render viewport: an x-first bounding box in <paramref name="Crs"/> plus
/// the output pixel size. Coordinates are in the CRS' own units.
/// </summary>
public sealed record RasterViewport(Envelope Bounds, int Width, int Height, string Crs)
{
    /// <summary>World units covered by one horizontal pixel; the natural simplify tolerance.</summary>
    public double UnitsPerPixel => Bounds.IsEmpty || Width <= 0 ? 0 : Bounds.Width / Width;

    /// <summary>Whether the viewport has a positive pixel size and a non-empty extent.</summary>
    public bool IsValid => !Bounds.IsEmpty && Width > 0 && Height > 0;
}

/// <summary>
/// A raw pixel buffer. Vector rasterization produces premultiplied RGBA; the
/// imagery pipeline normalises to straight (non-premultiplied) RGBA8888.
/// </summary>
public sealed record RasterBuffer(
    ReadOnlyMemory<byte> Pixels,
    int Width,
    int Height,
    int Stride,
    RasterPixelFormat PixelFormat,
    bool Premultiplied = true)
{
    /// <summary>Minimum bytes needed to hold all rows at <paramref name="stride"/>.</summary>
    public long RequiredLength => (long)Stride * Height;

    /// <summary>Bytes per pixel for the declared format.</summary>
    public int BytesPerPixel => PixelFormat == RasterPixelFormat.Rgba8888 ? 4 : 3;
}

/// <summary>An encoded image result: bytes only; the media type travels with it.</summary>
public sealed record RasterImage(byte[] Content, string MediaType, int Width, int Height, RasterFormat Format);

/// <summary>
/// One entry of the bottom-to-top input stack for
/// <see cref="IRasterOperations.CompositeAsync"/>.
/// </summary>
public abstract record RasterLayer;

/// <summary>An in-memory pixel buffer to blend into the stack.</summary>
public sealed record RasterBufferLayer(
    RasterBuffer Buffer,
    RasterBlend Blend = RasterBlend.Over,
    double Opacity = 1.0) : RasterLayer;

/// <summary>A configured imagery source (a key the host has resolved), never a caller-supplied URL.</summary>
public sealed record RasterSourceLayer(
    string Source,
    RasterBlend Blend = RasterBlend.Over,
    double Opacity = 1.0) : RasterLayer;
/// <summary>Read and normalise one configured imagery source to the requested format.</summary>
public sealed record RasterReadRequest(string Source, RasterViewport Viewport, RasterFormat Format = RasterFormat.Png);

/// <summary>
/// A bottom-to-top layer stack blended into one encoded image at the given
/// viewport size (configured imagery is resized to the frame).
/// </summary>
public sealed record RasterCompositeRequest(
    RasterViewport Viewport,
    IReadOnlyList<RasterLayer> Layers,
    RasterFormat Format = RasterFormat.Png,
    int Quality = 90,
    string? Background = null,
    bool Transparent = true);

/// <summary>
/// A temporal extent in epoch milliseconds (T-040, S2 export <c>time</c>):
/// an instant has equal bounds, a <c>null</c> bound is open (infinite).
/// Core-typed (two longs), so it crosses the contract boundary.
/// </summary>
public sealed record MapTimeExtent(long? StartMs, long? EndMs);

/// <summary>
/// One styled layer's resolved read services: the dataset key the style's
/// <c>source-layer</c> names, a keyed store, its catalogue, an optional
/// store-level filter in the provider's safe grammar and an optional
/// temporal extent. The renderer drops features whose date values fall
/// outside <see cref="Time"/>; features without date values always pass
/// (ArcGIS Server ignores <c>time</c> on non-time-aware layers). Resolved
/// by the host at the edge, so the renderer performs no service location.
/// </summary>
public sealed record MapLayerSource(
    string Dataset,
    IFeatureStore Features,
    IDataCatalogue Catalogue,
    string? Filter = null,
    MapTimeExtent? Time = null);

/// <summary>
/// A complete render request: viewport, style document, resolved layer
/// sources, optional configured imagery, and output encoding.
/// </summary>
public sealed record MapRenderRequest(
    RasterViewport Viewport,
    string Style,
    IReadOnlyList<MapLayerSource> Layers,
    IReadOnlyList<RasterSourceLayer>? Imagery = null,
    RasterFormat Format = RasterFormat.Png,
    int Quality = 90,
    string? Background = null,
    bool Transparent = true,
    double Scale = 1.0);

/// <summary>
/// Renders styled vector layers over imagery to one encoded image: read
/// (<see cref="IFeatureStore"/>, <see cref="IDataCatalogue"/>), shape
/// (<see cref="IGeometryOperations"/>), place
/// (<see cref="ICoordinateTransforms"/>), rasterize, compose and encode.
/// </summary>
public interface IMapRenderer
{
    Task<RasterImage> RenderAsync(MapRenderRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Imagery verbs over raw buffers: read and normalise a configured source,
/// blend a bottom-to-top layer stack and encode the result. Implemented by
/// <c>Spatial.Imagery.Vips</c>; the vector renderer talks only to this
/// interface (ADR-0033/ADR-0044).
/// </summary>
public interface IRasterOperations
{
    Task<RasterImage> ReadAsync(RasterReadRequest request, CancellationToken cancellationToken = default);

    Task<RasterImage> CompositeAsync(RasterCompositeRequest request, CancellationToken cancellationToken = default);
}
