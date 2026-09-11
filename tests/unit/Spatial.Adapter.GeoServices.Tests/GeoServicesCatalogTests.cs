namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The static service map built from configuration: the Geometry service is
/// always present, the root is normalised, and invalid or duplicate entries
/// fail fast at startup.
/// </summary>
public sealed class GeoServicesCatalogTests
{
    private static GeoServicesServiceOptions Service(string name, string store, string type = "FeatureServer") =>
        new() { Name = name, Store = store, Type = type };

    [Fact]
    public void The_catalog_always_advertises_the_geometry_service_first()
    {
        var catalog = new GeoServicesCatalog(new GeoServicesOptions { Services = [Service("demo", "demo")] });

        Assert.Equal(2, catalog.Services.Count);
        Assert.Equal(GeoServicesCatalog.GeometryServiceName, catalog.Services[0].Name);
        Assert.Equal("GeometryServer", catalog.Services[0].Type);
        Assert.Equal("demo", catalog.Services[1].Name);
        Assert.Equal("demo", catalog.Services[1].Store);
    }

    [Theory]
    [InlineData("arcgis/rest/services", "/arcgis/rest/services")]
    [InlineData("/custom/", "/custom")]
    [InlineData("  /trimmed  ", "/trimmed")]
    public void The_root_is_normalised(string root, string expected)
    {
        var catalog = new GeoServicesCatalog(new GeoServicesOptions { Root = root });

        Assert.Equal(expected, catalog.Root);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_root_fails_fast(string root)
    {
        Assert.Throws<InvalidOperationException>(() => new GeoServicesCatalog(new GeoServicesOptions { Root = root }));
    }

    [Fact]
    public void TryGet_finds_a_configured_service_case_insensitively()
    {
        var catalog = new GeoServicesCatalog(new GeoServicesOptions { Services = [Service("demo", "demo")] });

        Assert.True(catalog.TryGet("DEMO", out var entry));
        Assert.Equal("demo", entry.Store);
        Assert.False(catalog.TryGet("missing", out _));
    }

    [Fact]
    public void A_service_missing_its_name_or_store_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new GeoServicesCatalog(new GeoServicesOptions { Services = [Service("", "demo")] }));
        Assert.Throws<InvalidOperationException>(() =>
            new GeoServicesCatalog(new GeoServicesOptions { Services = [Service("demo", "")] }));
    }

    [Fact]
    public void A_duplicate_service_name_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new GeoServicesCatalog(new GeoServicesOptions { Services = [Service("demo", "a"), Service("DEMO", "b")] }));
    }

    [Fact]
    public void An_unsupported_service_type_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new GeoServicesCatalog(new GeoServicesOptions { Services = [Service("demo", "demo", "MapServer")] }));
    }

    [Fact]
    public void A_null_options_argument_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new GeoServicesCatalog(null!));
    }
}
