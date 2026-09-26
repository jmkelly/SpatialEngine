using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Spatial.Host.Tests;

/// <summary>
/// Attachment writes on a host with local auth enabled (ADR-0071 + ADR-0066):
/// the write guard asks <see cref="Spatial.Contracts.AuthGuard"/> for the admin
/// role instead of comparing the static admin token, so an admin session bearer
/// authorizes the write while a missing bearer is a token-required error and a
/// signed-in user without the role is an invalid token.
/// </summary>
public sealed class GeoServicesAttachmentAuthTests : IDisposable
{
    private static readonly PasswordHasher<User> Hasher = new();
    private const string AdminPassword = "correct horse battery staple";
    private const string MemberPassword = "open sesame";
    private const string Root = "/arcgis/rest/services";
    private const string Service = $"{Root}/attach/FeatureServer";

    private const string GeoJson = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[13.4,52.5]},"properties":{"name":"Berlin","population":3664000}}
        ]}
        """;

    private static readonly byte[] Photo = [1, 2, 3, 4, 5, 6, 7, 8];

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-attachment-auth-").FullName;
    private readonly WebApplicationFactory<Program> _factory;

    public GeoServicesAttachmentAuthTests() => _factory = new AttachmentAuthFactory(Path.Combine(_directory, "maps.json"));

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task An_admin_session_bearer_authorizes_an_attachment_write()
    {
        using var client = _factory.CreateClient();
        var admin = await LoginAsync(client, "alice", AdminPassword);
        await SeedAsync(client, admin);

        var added = await SendAsync(client, admin, Upload(), $"{Service}/0/1/addAttachment?f=json");
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var result = JsonDocument.Parse(await added.Content.ReadAsStringAsync())
            .RootElement.GetProperty("addAttachmentResult");
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(1, result.GetProperty("objectId").GetInt64());
    }

    [Fact]
    public async Task A_missing_bearer_is_a_token_required_attachment_error()
    {
        using var client = _factory.CreateClient();
        var admin = await LoginAsync(client, "alice", AdminPassword);
        await SeedAsync(client, admin);

        using var anonymous = await client.PostAsync($"{Service}/0/1/addAttachment?f=json", Upload());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var error = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal(498, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task A_signed_in_user_without_the_admin_role_is_an_invalid_token_error()
    {
        using var client = _factory.CreateClient();
        var admin = await LoginAsync(client, "alice", AdminPassword);
        await SeedAsync(client, admin);
        var member = await LoginAsync(client, "bob", MemberPassword);

        var refused = await SendAsync(client, member, Upload(), $"{Service}/0/1/addAttachment?f=json");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var error = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal(497, error.GetProperty("code").GetInt32());
    }

    private static async Task<string> LoginAsync(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();
        Assert.NotNull(token);
        return token;
    }

    private static async Task SeedAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/ingest?store=memory&dataset=attach.places&srid=4326&format=geojson&publish=attach")
        {
            Content = new StringContent(GeoJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var ingested = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, ingested.StatusCode);
    }

    private static MultipartFormDataContent Upload()
    {
        var upload = new MultipartFormDataContent();
        var file = new ByteArrayContent(Photo);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        upload.Add(file, "attachment", "photo.jpg");
        return upload;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string token, MultipartFormDataContent upload, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = upload };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private sealed record User(string Name);

    private sealed class AttachmentAuthFactory(string mapsPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Maps:Path", mapsPath);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Spatial:Auth:Users:0:Username"] = "alice",
                    ["Spatial:Auth:Users:0:PasswordHash"] = Hasher.HashPassword(new User("alice"), AdminPassword),
                    ["Spatial:Auth:Users:0:Roles:0"] = "admin",
                    ["Spatial:Auth:Users:1:Username"] = "bob",
                    ["Spatial:Auth:Users:1:PasswordHash"] = Hasher.HashPassword(new User("bob"), MemberPassword),
                }));
        }
    }
}
