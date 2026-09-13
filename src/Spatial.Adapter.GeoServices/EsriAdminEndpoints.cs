using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Spatial.Interop.Esri;
using Spatial.Interop.Ingest;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Esri admin projection (ADR-0041 §5): a thin, token-gated mapping of the
/// ArcGIS REST admin surface onto the neutral publication registry and ingest
/// capability. It is a projection, not the model — service creation goes
/// through <see cref="IMapRegistry.PutAsync"/> and uploads through
/// <see cref="IDatasetIngest"/>. Data-store registration, definitions,
/// portal items and everything the v1.0 specification does not describe are
/// rejected. Uploads are staged in memory with a TTL and the configured byte
/// cap before being published.
/// </summary>
public static class EsriAdminEndpoints
{
    /// <summary>Maps the admin projection at <see cref="EsriAdminOptions.Root"/>.</summary>
    public static void Map(IEndpointRouteBuilder app, EsriAdminOptions options, IMapRegistry registry)
    {
        var staging = new EsriUploadStaging(options.UploadTtl);
        var group = app.MapGroup(options.Root);

        group.MapMethods("/services", ["GET", "POST"], (HttpContext context, CancellationToken token) =>
            Handle(options, context, () => ListServicesAsync(registry, token)));
        group.MapMethods("/services/{service}", ["GET", "POST"], (string service, HttpContext context, CancellationToken token) =>
            Handle(options, context, () => GetServiceAsync(registry, service, token)));
        group.MapPost("/services/{service}/createService", (string service, HttpContext context, CancellationToken token) =>
            Handle(options, context, () => CreateServiceAsync(registry, service, context, token)));
        group.MapPost("/services/{service}/deleteService", (string service, HttpContext context, CancellationToken token) =>
            Handle(options, context, () => DeleteServiceAsync(registry, service, token)));
        group.MapPost("/uploads", (HttpContext context, IServiceProvider services, CancellationToken token) =>
            Handle(options, context, () => UploadAsync(options, staging, context, services, token)));
        group.MapPost("/uploads/{id}/publish", (string id, HttpContext context, CancellationToken token) =>
            Handle(options, context, () => PublishAsync(registry, staging, id, context, token)));
    }

    private static async Task<IResult> Handle(EsriAdminOptions options, HttpContext context, Func<Task<IResult>> action)
    {
        try
        {
            EnsureAuthorized(options, context);
            return await action();
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> ListServicesAsync(IMapRegistry registry, CancellationToken token)
    {
        var services = (await registry.ListAsync(token))
            .Where(map => map.Exposes(MapService.Feature))
            .Select(map => new AdminServiceEntry(map.Name, "FeatureServer"))
            .ToArray();
        return EsriJson.Value(new AdminServiceList(services));
    }

    private static async Task<IResult> GetServiceAsync(IMapRegistry registry, string service, CancellationToken token)
    {
        var name = ServiceName(service);
        var map = await registry.GetAsync(name, token);
        if (!map.Exposes(MapService.Feature))
        {
            throw EsriInteropException.Invalid($"Map '{name}' does not expose a FeatureServer.");
        }

        var layers = map.Layers
            .Where(layer => layer.Kind == MapLayerKind.Feature)
            .Select(layer => new AdminLayer(layer.LayerId, layer.Name ?? layer.Dataset, layer.Dataset))
            .ToArray();
        return EsriJson.Value(new AdminService(name, "FeatureServer", map.Store, layers));
    }

    private static async Task<IResult> CreateServiceAsync(
        IMapRegistry registry, string service, HttpContext context, CancellationToken token)
    {
        var name = ServiceName(service);
        var form = await context.Request.ReadFormAsync(token);
        var store = Form(form, "store", context) ?? throw EsriInteropException.Invalid("A 'store' is required.");
        var dataset = Form(form, "dataset", context) ?? throw EsriInteropException.Invalid("A 'dataset' is required.");
        var map = new Map(name, store, [new MapLayer(dataset, -1)], [MapService.Feature]);
        var stored = await registry.PutAsync(map, token);
        return EsriJson.Value(new AdminSuccess(true, name, stored.Layers.Select(layer => layer.LayerId).ToArray()));
    }

    private static async Task<IResult> DeleteServiceAsync(IMapRegistry registry, string service, CancellationToken token)
    {
        var name = ServiceName(service);
        return EsriJson.Value(new AdminSuccess(await registry.DeleteAsync(name, token), name, []));
    }

    private static async Task<IResult> UploadAsync(
        EsriAdminOptions options, EsriUploadStaging staging, HttpContext context, IServiceProvider services, CancellationToken token)
    {
        var query = context.Request.Query;
        var store = Query(query, "store") ?? "memory";
        var dataset = Query(query, "dataset") ?? throw EsriInteropException.Invalid("A 'dataset' query parameter is required.");
        var format = ParseFormat(Query(query, "format") ?? "geojson");
        var srid = ParseSrid(Query(query, "srid"));
        var identityField = Query(query, "identityField");
        var target = services.GetKeyedService<IDatasetIngest>(store)
            ?? throw EsriInteropException.Invalid($"Store '{store}' does not support ingest.");

        await using var body = await ReadUploadAsync(context.Request, options.MaxBytes);
        var decoded = DatasetDecoder.Decode(body, format, new DecodeOptions
        {
            Srid = srid,
            BatchSize = options.BatchSize,
            IdentityField = identityField,
        });
        var outcome = await target.IngestAsync(new IngestRequest(dataset, srid, IdentityOf(Query(query, "identity")), identityField), decoded.Pages, token);
        var itemId = staging.Stage(outcome, store);
        return EsriJson.Value(new AdminUpload(true, new AdminItem(itemId)));
    }

    private static async Task<IResult> PublishAsync(
        IMapRegistry registry, EsriUploadStaging staging, string id, HttpContext context, CancellationToken token)
    {
        var staged = staging.Take(id) ?? throw EsriInteropException.Invalid($"The staged upload '{id}' does not exist or has expired.");
        var form = await context.Request.ReadFormAsync(token);
        var name = Form(form, "name", context)
            ?? Query(context.Request.Query, "name")
            ?? throw EsriInteropException.Invalid("A 'name' is required to publish the upload.");
        var map = new Map(name, staged.Store, [new MapLayer(staged.Outcome.Dataset, -1)], [MapService.Feature]);
        var stored = await registry.PutAsync(map, token);
        return EsriJson.Value(new AdminSuccess(true, stored.Name, stored.Layers.Select(layer => layer.LayerId).ToArray()));
    }

    /// <summary>Validates the admin token, in constant time; without a configured token the projection is unavailable.</summary>
    private static void EnsureAuthorized(EsriAdminOptions options, HttpContext context)
    {
        if (!options.Enabled)
        {
            throw new EsriInteropException(
                EsriErrorCodes.ServiceUnavailable,
                "The Esri admin projection is not configured; set Spatial:Admin:Token or SPATIAL_ADMIN_TOKEN.");
        }

        var presented = PresentedToken(context) ?? throw new EsriInteropException(
            EsriErrorCodes.TokenRequired, "An admin token is required.");
        if (!FixedTimeEquals(presented, options.Token))
        {
            throw new EsriInteropException(EsriErrorCodes.InvalidToken, "The admin token is not valid.");
        }
    }

    private static string? PresentedToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }

        var query = Query(context.Request.Query, "token");
        return query;
    }

    private static bool FixedTimeEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static string ServiceName(string service)
    {
        var dot = service.IndexOf('.');
        if (dot <= 0 || dot == service.Length - 1)
        {
            throw EsriInteropException.Invalid($"Service '{service}' must be named as '<name>.<type>'.");
        }

        var name = service[..dot];
        var type = service[(dot + 1)..];
        if (!string.Equals(type, "FeatureServer", StringComparison.OrdinalIgnoreCase))
        {
            throw EsriInteropException.Invalid($"Service type '{type}' is not supported; only FeatureServer is exposed.");
        }

        return name;
    }

    private static async Task<MemoryStream> ReadUploadAsync(HttpRequest request, long maxBytes)
    {
        var form = await request.ReadFormAsync();
        var file = form.Files.Count > 0
            ? form.Files[0]
            : throw EsriInteropException.Invalid("The multipart upload carries no file part.");
        if (file.Length > maxBytes)
        {
            throw EsriInteropException.Invalid($"The upload is {file.Length} bytes, above the configured maximum of {maxBytes}.");
        }

        var buffer = new MemoryStream();
        await using var source = file.OpenReadStream();
        await source.CopyToAsync(buffer);
        buffer.Position = 0;
        return buffer;
    }

    private static readonly Dictionary<string, IngestFormat> Formats = new(StringComparer.OrdinalIgnoreCase)
    {
        ["geojson"] = IngestFormat.GeoJson,
        ["ndjson"] = IngestFormat.NewlineDelimitedGeoJson,
        ["geojsonl"] = IngestFormat.NewlineDelimitedGeoJson,
        ["csv"] = IngestFormat.Csv,
    };

    private static readonly Dictionary<string, IngestIdentity> Identities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["auto"] = IngestIdentity.Auto,
        ["none"] = IngestIdentity.None,
        ["source"] = IngestIdentity.Source,
    };

    private static IngestFormat ParseFormat(string name) =>
        Formats.TryGetValue(name, out var format)
            ? format
            : throw EsriInteropException.Invalid($"Format '{name}' is not a supported ingest format.");

    private static int ParseSrid(string? value) =>
        int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var srid) && srid > 0
            ? srid
            : throw EsriInteropException.Invalid($"The 'srid' query parameter must be a positive integer, got '{value}'.");

    private static IngestIdentity IdentityOf(string? name) =>
        string.IsNullOrEmpty(name)
            ? IngestIdentity.Auto
            : Identities.TryGetValue(name, out var identity)
                ? identity
                : throw EsriInteropException.Invalid($"Unknown identity mode '{name}'.");

    private static string? Query(IQueryCollection query, string key)
    {
        var value = query[key].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? Form(IFormCollection form, string key, HttpContext context) =>
        form.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : Query(context.Request.Query, key);
}

/// <summary>In-memory staging of uploaded, not-yet-published datasets with a TTL (ADR-0041 §6).</summary>
internal sealed class EsriUploadStaging(TimeSpan ttl)
{
    private readonly ConcurrentDictionary<string, StagedUpload> _items = new(StringComparer.Ordinal);

    /// <summary>Stages an ingest outcome and returns its item id.</summary>
    public string Stage(IngestOutcome outcome, string store)
    {
        Prune();
        var id = Guid.NewGuid().ToString("N");
        _items[id] = new StagedUpload(outcome, store, DateTimeOffset.UtcNow);
        return id;
    }

    /// <summary>Removes and returns a staged upload, or null when missing/expired.</summary>
    public StagedUpload? Take(string id)
    {
        Prune();
        return _items.TryRemove(id, out var staged) ? staged : null;
    }

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - ttl;
        foreach (var (key, value) in _items)
        {
            if (value.CreatedAt < cutoff)
            {
                _items.TryRemove(key, out _);
            }
        }
    }
}

/// <summary>One staged upload: the ingest outcome and the target store.</summary>
internal sealed record StagedUpload(IngestOutcome Outcome, string Store, DateTimeOffset CreatedAt);

/// <summary>The admin service list envelope.</summary>
internal sealed record AdminServiceList(IReadOnlyList<AdminServiceEntry> Services);

/// <summary>One admin service entry.</summary>
internal sealed record AdminServiceEntry(string Name, string Type);

/// <summary>One admin service description.</summary>
internal sealed record AdminService(string Name, string Type, string Store, IReadOnlyList<AdminLayer> Layers);

/// <summary>One admin layer description.</summary>
internal sealed record AdminLayer(int Id, string Name, string Dataset);

/// <summary>The upload staging response.</summary>
internal sealed record AdminUpload(bool Success, AdminItem Item);

/// <summary>The staged item reference.</summary>
internal sealed record AdminItem(string ItemId);

/// <summary>The create/delete/publish result.</summary>
internal sealed record AdminSuccess(bool Success, string ServiceName, IReadOnlyList<int> LayerIds);
