using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.Core.Geometry;

namespace Spatial.Spike.TileCache;

/// <summary>
/// The fidelity check the u2x.1 spike also carries.
///
/// <c>Mirror.cs</c> reproduces <c>Spatial.Host</c>'s <c>internal</c> version
/// expression, so the spike keys by the same bytes the host does. A spike that
/// had the key path subtly wrong would report a fan-out for a key nothing
/// serves, so with <c>--host=URL</c> the spike checks two things against a real
/// host running this branch:
///
/// 1. <b>Shape</b> — the version the host reports is the 64 hex characters
///    <c>MapRenderEngine.Version</c> produces, and it is stable across repeated
///    requests for the same map.
/// 2. <b>Mechanism</b> — a single-layer write and a single-layer style save both
///    move it, which is the behaviour the fan-out measurement rests on.
///
/// The two harnesses run against different stores, so the tokens are not
/// compared byte-for-byte; what is compared is that the host moves its key in
/// exactly the situations this spike measures a fan-out for.
/// </summary>
internal static class HostCheck
{
    private const string Token = "spike-admin-token";

    private static readonly string[] Services = ["tiles"];

    internal static async Task RunAsync(
        string host,
        string service,
        IReadOnlyList<LayerSpec> layers,
        IReadOnlyList<TileCoordinate> tiles,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"## Mirror fidelity against {host}");
        Console.WriteLine("# the spike's store and the host's are different, so the tokens are not compared byte-for-byte.");
        Console.WriteLine("# What is checked is that the host moves its tile key in exactly the situations this spike measures a fan-out for.");

        var tile = tiles[0];
        var address = $"/api/maps/{service}/tiles/{tile.Z}/{tile.X}/{tile.Y}.png";
        var dataset = $"spike.{Guid.NewGuid():N}";

        using var client = new HttpClient { BaseAddress = new Uri(host) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        var published = await PublishAsync(client, service, dataset, layers[0].Style, cancellationToken);
        if (!published)
        {
            Console.WriteLine($"# SKIPPED: the host at {host} refused the spike's map; the mirror was NOT checked and the numbers above stand unverified against a host.");
            return;
        }

        var first = await VersionAsync(client, address, cancellationToken);
        var second = await VersionAsync(client, address, cancellationToken);
        Console.WriteLine($"# stable across repeats : {(first is not null && first == second ? "yes" : "NO")}  ({first ?? "no X-Tile-Version header"})");
        Console.WriteLine($"# 64 hex characters     : {(first is { Length: 64 } ? "yes" : "NO")}");

        // A single-layer data write must move the key (ADR-0083).
        var schema = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]);
        var feature = new Feature(
            FeatureId.Unassigned,
            schema,
            [
                AttributeValue.FromString("spike-fidelity"),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(13.404, 52.520, CoordinateReference.Epsg(4326))),
            ]);
        var write = Convert.ToBase64String(FeatureBatchCodec.Encode(new FeatureBatch(schema, [feature])));
        using (var writeResponse = await client.PostAsJsonAsync(
            "/api/features/write?store=memory",
            new { dataset, features = write },
            cancellationToken))
        {
            writeResponse.EnsureSuccessStatusCode();
        }

        var afterWrite = await VersionAsync(client, address, cancellationToken);
        Console.WriteLine($"# moves on a layer write : {(first is not null && afterWrite is not null && first != afterWrite ? "yes" : "NO")}");

        // A single-layer style save must move it too.
        await PutMapAsync(client, service, dataset, layers[0].Dataset, Basemap.Restyle(layers[0]), cancellationToken);
        var afterStyle = await VersionAsync(client, address, cancellationToken);
        Console.WriteLine($"# moves on a style save  : {(afterWrite is not null && afterStyle is not null && afterWrite != afterStyle ? "yes" : "NO")}");

        var _ = address;
    }

    private static async Task<string?> VersionAsync(HttpClient client, string address, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(address, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return response.Headers.TryGetValues("X-Tile-Version", out var values) ? values.FirstOrDefault() : null;
    }

    private static async Task<bool> PublishAsync(
        HttpClient client, string service, string dataset, string style, CancellationToken cancellationToken)
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]);
        var feature = new Feature(
            FeatureId.Unassigned,
            schema,
            [
                AttributeValue.FromString("spike-fidelity"),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(13.404, 52.520, CoordinateReference.Epsg(4326))),
            ]);
        var batch = Convert.ToBase64String(FeatureBatchCodec.Encode(new FeatureBatch(schema, [feature])));

        using var create = await client.PostAsJsonAsync(
            "/api/datasets?store=memory",
            new { dataset, features = batch, srid = 4326 },
            cancellationToken);
        if (!create.IsSuccessStatusCode)
        {
            return false;
        }

        using var write = await client.PostAsJsonAsync(
            "/api/features/write?store=memory",
            new { dataset, features = batch },
            cancellationToken);
        if (!write.IsSuccessStatusCode)
        {
            return false;
        }

        return await PutMapAsync(client, service, dataset, dataset, style, cancellationToken);
    }

    private static async Task<bool> PutMapAsync(
        HttpClient client, string service, string dataset, string name, string style, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            name = service,
            store = "memory",
            services = Services,
            layers = new[] { new { dataset, layerId = 0, name, style } },
        });
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/maps/{service}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var response = await client.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
