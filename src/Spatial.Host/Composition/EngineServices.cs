using Microsoft.Extensions.Configuration;
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
        // Datum shift grids (ADR-0105) are configured, not embedded: a host
        // names the directories its bundles live in and a grid is deployed by
        // dropping a file into one. Nothing changes for a host that configures
        // none — every datum is shifted the classic Helmert way.
        builder.Services.AddSingleton(new ProjNetTransforms(GridDirectories(builder.Configuration)));
        builder.Services.AddSingleton<ICrsDirectory>(services => services.GetRequiredService<ProjNetTransforms>());
        builder.Services.AddSingleton<ICoordinateTransforms>(services => services.GetRequiredService<ProjNetTransforms>());
        // ADR-0075: the ground-distance buffer is the transformation
        // provider's verb (it owns the working plane) composed with the
        // planar buffer and the dissolve, not a new algorithm of its own.
        builder.Services.AddSingleton<ProjNetGeodesicBuffering>();
        builder.Services.AddSingleton<IGeodesicBuffering>(services => services.GetRequiredService<ProjNetGeodesicBuffering>());
    }

    /// <summary>
    /// The configured grid directories, in priority order, read from
    /// <c>Spatial:Grids:Directories</c> (ADR-0105). The list is a priority
    /// order rather than a set, so the first directory holding a bundle wins
    /// and an operator can shadow a shipped default without editing it. A host
    /// that configures none deploys no grid and says so in every
    /// transformation's method text.
    /// </summary>
    private static string[] GridDirectories(ConfigurationManager configuration) =>
        [.. configuration.GetSection("Spatial:Grids:Directories").Get<string[]>() ?? []];
}
