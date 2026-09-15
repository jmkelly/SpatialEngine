using Spatial.Contracts;
using Spatial.Contracts.Transformations;
using Spatial.Operations.NetTopologySuite;
using Spatial.Transformations.ProjNet;

namespace Spatial.Host;

/// <summary>
/// Registers the in-process geometry and transformation services (ADR-0033,
/// ADR-0036): concrete NTS/ProjNet implementations exposed through the
/// granular SDK interfaces. Split from the composition root so its fan-out
/// stays deliberate (ADR-0040).
/// </summary>
internal static class EngineServices
{
    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IGeometryOperations, NtsGeometryOperations>();
        builder.Services.AddSingleton<NtsGeometryMeasures>();
        builder.Services.AddSingleton<IGeometryMeasures>(services => services.GetRequiredService<NtsGeometryMeasures>());
        builder.Services.AddSingleton<NtsGeometryProcessing>();
        builder.Services.AddSingleton<IGeometryProcessing>(services => services.GetRequiredService<NtsGeometryProcessing>());
        builder.Services.AddSingleton<NtsGeometryRelations>();
        builder.Services.AddSingleton<IGeometryRelations>(services => services.GetRequiredService<NtsGeometryRelations>());
        builder.Services.AddSingleton<ProjNetTransforms>();
        builder.Services.AddSingleton<ICrsDirectory>(services => services.GetRequiredService<ProjNetTransforms>());
        builder.Services.AddSingleton<ICoordinateTransforms>(services => services.GetRequiredService<ProjNetTransforms>());
    }
}
