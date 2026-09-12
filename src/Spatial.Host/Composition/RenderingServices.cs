using Spatial.Host.Api;
using Spatial.Imagery.Vips;
using Spatial.PluginSdk;
using Spatial.Rendering.Skia;

namespace Spatial.Host;

/// <summary>
/// Registers the vector renderer and the imagery pipeline (ADR-0044): the
/// configured rendering/imagery options, the Vips-backed raster operations
/// and the Skia map renderer behind the SDK's <see cref="IRasterOperations"/>
/// and <see cref="IMapRenderer"/> faces. Split from the composition root so
/// its fan-out stays deliberate (ADR-0040).
/// </summary>
internal static class RenderingServices
{
    public static void Configure(WebApplicationBuilder builder)
    {
        var renderingOptions = builder.Configuration.GetSection("Spatial:Rendering").Get<RenderingOptions>()
            ?? new RenderingOptions();
        var imageryOptions = builder.Configuration.GetSection("Spatial:Imagery").Get<ImageryOptions>()
            ?? new ImageryOptions();
        builder.Services.AddSingleton(renderingOptions);
        builder.Services.AddSingleton(imageryOptions);
        builder.Services.AddSingleton<IRasterOperations>(new VipsRasterOperations(imageryOptions.ToSourceMap()));
        builder.Services.AddSingleton<IMapRenderer>(services => new MapRenderer(
            services.GetRequiredService<ICoordinateTransforms>(),
            services.GetRequiredService<IGeometryOperations>(),
            services.GetRequiredService<IRasterOperations>(),
            new RenderLimits(renderingOptions.MaxPixels, renderingOptions.MaxLayers)));
    }
}
