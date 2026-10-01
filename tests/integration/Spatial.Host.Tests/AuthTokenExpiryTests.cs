using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Spatial.Host.Tests;

/// <summary>
/// The bearer lifetime boundary of the local auth issuer (ADR-0071), on a
/// host whose token lifetime is short enough to expire on its own.
/// </summary>
/// <remarks>
/// This test is a class of its own because <c>Spatial:Auth:TokenLifetime</c>
/// is read once, when the host boots, and a shared host has one lifetime for
/// the class: ADR-0161's Split verdict on
/// <see cref="AuthEndpointTests"/>, with the four tests that share the class's
/// configuration converted onto one host and this one left per-test.
/// </remarks>
public sealed class AuthTokenExpiryTests
{
    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        using var factory = new ExpiringAuthFactory();
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { username = "alice", password = AuthEndpointTests.Password });
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await Task.Delay(2);
        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class ExpiringAuthFactory : SpatialHostFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Spatial:Auth:Users:0:Username"] = "alice",
                    ["Spatial:Auth:Users:0:PasswordHash"] = AuthEndpointTests.Hash,
                    ["Spatial:Auth:Users:0:Roles:0"] = "admin",
                    ["Spatial:Auth:TokenLifetime"] = "00:00:00.0000001",
                }));
        }
    }
}
