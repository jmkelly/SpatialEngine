using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Spatial.PluginSdk.Providers;

namespace Spatial.Host.Tests;

/// <summary>
/// T-060 host wiring (T-088 extends it to PostGIS): the additive
/// <c>IFeatureAttachmentStore</c> capability resolves for the memory store
/// and the PostGIS sidecar table, and is absent everywhere else (demo is
/// read-only). T-061 serves the
/// HTTP surface on the capability: layers backed by the memory store
/// advertise <c>hasAttachments</c>, while capability-less stores keep the
/// ADR-0061 honesty (empty reads, typed write rejects, unadvertised).
/// </summary>
public sealed class AttachmentStoreWiringTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new WiringFactory();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void The_memory_store_exposes_the_attachment_capability()
    {
        var stores = _factory.Services.GetRequiredService<IStoreRegistry>();

        Assert.NotNull(stores.AttachmentStore("memory"));
    }

    [Fact]
    public void The_postgis_store_exposes_the_attachment_capability()
    {
        var stores = _factory.Services.GetRequiredService<IStoreRegistry>();

        Assert.NotNull(stores.AttachmentStore("postgis"));
    }

    [Fact]
    public void Stores_without_blob_support_expose_no_attachment_capability()
    {
        var stores = _factory.Services.GetRequiredService<IStoreRegistry>();

        Assert.Null(stores.AttachmentStore("demo"));
    }

    private sealed class WiringFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", "test-admin-token");
        }
    }
}
