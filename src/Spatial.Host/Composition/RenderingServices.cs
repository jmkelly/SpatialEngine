using Spatial.Host.Api;
using Spatial.Host.Tiling;
using Spatial.Imagery.Vips;
using Spatial.PluginSdk;
using Spatial.Rendering.Skia;
using Spatial.Tiling.WebMercator;

namespace Spatial.Host;

/// <summary>
/// Registers the vector renderer and the imagery pipeline (ADR-0044) plus the
/// tile scheme, cache and orchestration (ADR-0046): the configured
/// rendering/imagery/tile options, the Vips-backed raster operations, the
/// Skia map renderer, the Web-Mercator scheme, the in-memory tile cache and
/// the tile service. Split from the composition root so its fan-out stays
/// deliberate (ADR-0040).
/// </summary>
internal static class RenderingServices
{
    public static void Configure(WebApplicationBuilder builder)
    {
        var renderingOptions = builder.Configuration.GetSection("Spatial:Rendering").Get<RenderingOptions>()
            ?? new RenderingOptions();
        var imageryOptions = builder.Configuration.GetSection("Spatial:Imagery").Get<ImageryOptions>()
            ?? new ImageryOptions();
        var tileOptions = builder.Configuration.GetSection("Spatial:Tiles").Get<TileOptions>()
            ?? new TileOptions();
        builder.Services.AddSingleton(renderingOptions);
        builder.Services.AddSingleton(imageryOptions);
        builder.Services.AddSingleton(tileOptions);
        builder.Services.AddSingleton<IRasterOperations>(new VipsRasterOperations(imageryOptions.ToSourceMap()));
        builder.Services.AddSingleton<IMapRenderer>(services => new MapRenderer(
            services.GetRequiredService<ICoordinateTransforms>(),
            services.GetRequiredService<IGeometryOperations>(),
            services.GetRequiredService<IRasterOperations>(),
            new RenderLimits(renderingOptions.MaxPixels, renderingOptions.MaxLayers)));

        // Tiles: the scheme is pluggable (a second projection is another
        // ITileScheme registration); the initial cache owns process memory and
        // can be replaced without touching the routes (ADR-0046).
        builder.Services.AddSingleton<ITileScheme>(new WebMercatorTileScheme());
        builder.Services.AddSingleton<ITileCache>(new InMemoryTileCache(tileOptions.Cache));
        builder.Services.AddSingleton(services => new TileService(
            services.GetRequiredService<IMapRenderer>(),
            services.GetServices<ITileScheme>(),
            services.GetRequiredService<ITileCache>(),
            services.GetRequiredService<TileOptions>()));
    }
}
