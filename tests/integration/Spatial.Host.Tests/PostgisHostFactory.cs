using Microsoft.AspNetCore.Hosting;

namespace Spatial.Host.Tests;

/// <summary>
/// The default host under test with the shared PostGIS container configured
/// as <c>Spatial:Postgis:ConnectionString</c> (ADR-0028, ADR-0033): the host
/// integration suite therefore runs against a real PostGIS store and no
/// operator sets <c>SPATIAL_POSTGIS_CONNECTION</c>. When Docker is not
/// reachable the store stays unconfigured, the host logs the
/// <c>store.unavailable</c> warning and every test still passes — the
/// containerised tests skip instead, through
/// <see cref="PostgisTestDatabase"/>. It is
/// <see cref="SpatialHostFactory"/> with the store wired in, so it inherits
/// the suite's client timeout.
/// </summary>
public class PostgisHostFactory : SpatialHostFactory
{
    /// <summary>
    /// Adds this factory's host settings on top of the containerised store.
    /// Derived factories call it first, then their own settings.
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        var connectionString = PostgisTestDatabase.ConnectionString;
        if (connectionString is not null)
        {
            builder.UseSetting("Spatial:Postgis:ConnectionString", connectionString);
        }
    }
}
