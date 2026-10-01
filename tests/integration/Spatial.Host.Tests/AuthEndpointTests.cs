using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Contracts;

namespace Spatial.Host.Tests;

/// <summary>Phase-1 local username/password auth over the real host (ADR-0071).</summary>
/// <remarks>
/// One host for the class (ADR-0160, ADR-0161): the four tests here all
/// authenticate the same configured user and none of them publishes a map, so
/// there is no dataset identity to hand out. The one test that needs a
/// different host setting — an already-expired bearer, which it gets by
/// injecting <c>Spatial:Auth:TokenLifetime</c> — is
/// <see cref="AuthTokenExpiryTests"/>, because a shared host boots with one
/// lifetime and cannot honour a second.
/// </remarks>
public sealed class AuthEndpointTests : IClassFixture<AuthEndpointTests.AuthHost>
{
    internal const string Password = "correct horse battery staple";

    internal static readonly string Hash = new PasswordHasher<LocalUser>()
        .HashPassword(new LocalUser("alice"), Password);

    private readonly AuthHost _host;

    public AuthEndpointTests(AuthHost host) => _host = host;

    [Fact]
    public async Task Login_me_refresh_and_logout_manage_an_opaque_bearer()
    {
        using var client = _host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "alice", password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement;
        var token = body.GetProperty("token").GetString();
        Assert.NotNull(token);
        Assert.True(token.Length >= 40);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var me = await client.GetAsync("/api/auth/me");
        var meBody = JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("alice", meBody.GetProperty("username").GetString());
        Assert.Contains("admin", meBody.GetProperty("roles").EnumerateArray().Select(role => role.GetString() ?? ""));

        var refreshed = await client.PostAsync("/api/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var replacement = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();
        Assert.NotEqual(token, replacement);
        var old = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        old.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(old)).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", replacement);
        var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var afterLogout = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Bad_credentials_are_auth_failed_without_password_disclosure()
    {
        using var client = _host.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { username = "alice", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("auth.failed", body);
        Assert.DoesNotContain("wrong", body);
    }

    [Fact]
    public async Task A_configured_bearer_gates_neutral_mutation_but_reads_stay_anonymous()
    {
        using var client = _host.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "alice", password = Password });
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();

        var anonymous = await client.GetAsync("/api/maps");
        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        var denied = await client.PutAsync("/api/maps/x", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var bearer = new HttpRequestMessage(
            HttpMethod.Put, "/api/maps/x") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var allowed = await client.SendAsync(bearer);
        Assert.NotEqual(HttpStatusCode.Unauthorized, allowed.StatusCode);
    }

    [Fact]
    public async Task Cancellation_is_honoured_before_auth_work()
    {
        var auth = _host.Services.GetRequiredService<IAuthService>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            auth.LoginAsync("alice", Password, cancellation.Token));
    }

    /// <summary>
    /// One host for the class, with the configured local user and a
    /// per-class map file (ADR-0160).
    /// </summary>
    public sealed class AuthHost : ClassHostFixture
    {
        public AuthHost()
            : base("spatial-auth")
        {
        }

        protected override void ConfigureHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Maps:Path", MapsPath);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Spatial:Auth:Users:0:Username"] = "alice",
                    ["Spatial:Auth:Users:0:PasswordHash"] = Hash,
                    ["Spatial:Auth:Users:0:Roles:0"] = "admin",
                }));
        }
    }

    private sealed record LocalUser(string Name);
}