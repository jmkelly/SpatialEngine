using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Contracts;

namespace Spatial.Host.Tests;

/// <summary>
/// Composition tests for keyed remote stores. A configured ArcGIS REST
/// service is one adapter behind two read faces, not two independent
/// adapters accidentally created by DI wiring.
/// </summary>
public sealed class RemoteStoreWiringTests
{
    [Fact]
    public void Catalogue_and_feature_faces_share_one_remote_store_adapter()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Spatial:ArcGisRest:Services:0:Name", "remote");
                builder.UseSetting("Spatial:ArcGisRest:Services:0:Url", "https://example.invalid/FeatureServer");
            });

        using var scope = factory.Services.CreateScope();
        var catalogue = scope.ServiceProvider.GetRequiredKeyedService<IDataCatalogue>("remote");
        var features = scope.ServiceProvider.GetRequiredKeyedService<IFeatureStore>("remote");

        Assert.Same(catalogue, features);
    }
}
