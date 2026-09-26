using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// Smoke tests proving the spatial host is an independently executable
/// process: it serves health and metadata endpoints with no desktop shell,
/// no plugins loaded and nothing for the operator to configure (ADR-0018).
/// The PostGIS store is configured from the test container (ADR-0072), and
/// health does not depend on it.
/// </summary>
public sealed class HostSmokeTests : IClassFixture<PostgisHostFactory>
{
    private readonly PostgisHostFactory _factory;

    public HostSmokeTests(PostgisHostFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Live_endpoint_reports_live()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthStatus>();
        Assert.Equal("live", body?.Status);
    }

    [Fact]
    public async Task Ready_endpoint_reports_ready()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthStatus>();
        Assert.Equal("ready", body?.Status);
    }

    [Fact]
    public async Task Root_reports_host_identity()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HostInfo>();
        Assert.Equal("Spatial.Host", body?.Name);
        Assert.False(string.IsNullOrWhiteSpace(body?.Version));
    }

    private sealed record HealthStatus(string? Status);

    private sealed record HostInfo(string? Name, string? Version);
}
