using System.Security.Cryptography;
using System.Text;
using Spatial.Core.Features;
using Spatial.Interop.Ingest;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Host.Api;

/// <summary>
/// The neutral admin surface (ADR-0041 §5): publication CRUD and the single
/// ingest request. Mutation routes require the configured admin token (env
/// <c>SPATIAL_ADMIN_TOKEN</c>) compared in constant time; when no token is
/// configured they are not mounted at all, while the read routes stay
/// available. Ingest decodes a raw or multipart upload with
/// <see cref="DatasetDecoder"/> and loads it atomically through the target
/// store's <see cref="IDatasetIngest"/>. A <c>publish</c> query parameter
/// registers the uploaded dataset as a one-layer publication in the same
/// call and reports the partial state safely retryably.
/// </summary>
internal static class AdminEndpoints
{
    /// <summary>The store ingest defaults to: the always-available writable in-memory provider.</summary>
    public const string DefaultIngestStore = "memory";

    public static void Map(IEndpointRouteBuilder app, AdminOptions admin, IngestOptions ingest)
    {
        app.MapGet("/api/publications", ListPublications).Produces<IReadOnlyList<Publication>>();
        app.MapGet("/api/publications/{name}", GetPublication)
            .Produces<Publication>();

        if (!admin.Enabled)
        {
            return;
        }

        app.MapPut("/api/publications/{name}", (string name, Publication publication, HttpContext context, IPublicationRegistry registry, CancellationToken token) =>
            PutPublication(context, admin, name, publication, registry, token));
        app.MapDelete("/api/publications/{name}", (string name, HttpContext context, IPublicationRegistry registry, CancellationToken token) =>
            DeletePublication(context, admin, name, registry, token));
        app.MapPost("/api/ingest", (HttpContext context, IServiceProvider services, IPublicationRegistry registry, CancellationToken token) =>
            Ingest(context, admin, ingest, services, registry, token));
    }

    private static async Task<IResult> ListPublications(IPublicationRegistry registry, CancellationToken token)
    {
        try
        {
            return Results.Ok(await registry.ListAsync(token));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> GetPublication(string name, IPublicationRegistry registry, CancellationToken token)
    {
        try
        {
            return Results.Ok(await registry.GetAsync(Uri.UnescapeDataString(name), token));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> PutPublication(
        HttpContext context, AdminOptions admin, string name, Publication publication,
        IPublicationRegistry registry, CancellationToken token)
    {
        if (Authorize(context, admin) is { } rejection)
        {
            return rejection;
        }

        try
        {
            var stored = await registry.PutAsync(publication with { Name = Uri.UnescapeDataString(name) }, token);
            return Results.Ok(stored);
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> DeletePublication(
        HttpContext context, AdminOptions admin, string name, IPublicationRegistry registry, CancellationToken token)
    {
        if (Authorize(context, admin) is { } rejection)
        {
            return rejection;
        }

        try
        {
            return Results.Ok(await registry.DeleteAsync(Uri.UnescapeDataString(name), token));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Ingest(
        HttpContext context, AdminOptions admin, IngestOptions ingest,
        IServiceProvider services, IPublicationRegistry registry, CancellationToken token)
    {
        if (Authorize(context, admin) is { } rejection)
        {
            return rejection;
        }

        try
        {
            var query = context.Request.Query;
            var dataset = Required(query, "dataset");
            var srid = ParseSrid(Required(query, "srid"));
            var store = query["store"].ToString();
            if (string.IsNullOrWhiteSpace(store))
            {
                store = DefaultIngestStore;
            }

            var format = ParseFormat(ingest, query["format"].ToString());
            var identityField = EmptyToNull(query["identityField"].ToString());
            var identity = ParseIdentity(query["identity"].ToString());
            var sourceSrid = ParseOptionalSrid(query["sourceSrid"].ToString());
            var target = services.GetKeyedService<IDatasetIngest>(store)
                ?? throw SpatialException.BadArguments($"Store '{store}' does not support ingest.");

            var decoded = await DecodeAsync(context, ingest, format, sourceSrid ?? srid, identityField);
            var pages = ConvertIfNeeded(decoded.Pages, sourceSrid, srid, services, token);
            var outcome = await target.IngestAsync(
                new IngestRequest(dataset, srid, identity, identityField), pages, token);

            return Results.Ok(await WithPublicationAsync(registry, query["publish"].ToString(), store, outcome, token));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    /// <summary>Decodes the upload body (raw or multipart) under the byte and feature caps.</summary>
    private static async Task<DecodedDataset> DecodeAsync(
        HttpContext context, IngestOptions ingest, IngestFormat format, int srid, string? identityField)
    {
        await using var body = await ReadUploadAsync(context.Request, ingest.MaxBytes);
        var decoded = DatasetDecoder.Decode(body, format, new DecodeOptions
        {
            Srid = srid,
            BatchSize = ingest.BatchSize,
            IdentityField = identityField,
        });
        var features = decoded.Pages.Sum(page => (long)page.Count);
        if (features > ingest.MaxFeatures)
        {
            throw SpatialException.BadArguments(
                $"The upload has {features} features, above the configured maximum of {ingest.MaxFeatures}.");
        }

        return decoded;
    }

    private static async Task<IngestOutcome> WithPublicationAsync(
        IPublicationRegistry registry, string publish, string store, IngestOutcome outcome, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(publish))
        {
            return outcome;
        }

        var name = Uri.UnescapeDataString(publish);
        Publication? existing = null;
        try
        {
            existing = await registry.GetAsync(name, token);
        }
        catch (SpatialException exception) when (exception.Code == SpatialException.NotFound)
        {
        }

        var layers = existing?.Layers.ToList() ?? [];
        if (layers.TrueForAll(layer => !string.Equals(layer.Dataset, outcome.Dataset, StringComparison.Ordinal)))
        {
            var next = layers.Count == 0 ? 0 : layers.Max(layer => layer.LayerId) + 1;
            layers.Add(new PublicationLayer(outcome.Dataset, next));
        }

        var publication = await registry.PutAsync(
            existing is null
                ? new Publication(name, PublicationKind.Feature, store, layers)
                : existing with { Store = store, Layers = layers },
            token);
        return outcome with { Publication = publication };
    }

    /// <summary>Reads the request body into memory, enforcing the byte cap for raw and multipart bodies.</summary>
    private static async Task<MemoryStream> ReadUploadAsync(HttpRequest request, long maxBytes)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            var file = form.Files.Count > 0
                ? form.Files[0]
                : throw SpatialException.BadArguments("The multipart upload carries no file part.");
            if (file.Length > maxBytes)
            {
                throw SpatialException.BadArguments($"The upload is {file.Length} bytes, above the configured maximum of {maxBytes}.");
            }

            var multipart = new MemoryStream();
            await using var source = file.OpenReadStream();
            await source.CopyToAsync(multipart);
            multipart.Position = 0;
            return multipart;
        }

        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await request.Body.ReadAsync(chunk)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw SpatialException.BadArguments($"The upload exceeds the configured maximum of {maxBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }

    private static int ParseSrid(string value) =>
        int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var srid) && srid > 0
            ? srid
            : throw SpatialException.BadArguments($"The 'srid' query parameter must be a positive integer, got '{value}'.");

    private static int? ParseOptionalSrid(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var srid) && srid > 0
            ? srid
            : throw SpatialException.BadArguments($"The 'sourceSrid' query parameter must be a positive integer, got '{value}'.");
    }

    /// <summary>
    /// Reprojects decoded pages from the source CRS to the target SRID through
    /// the engine's transform service (ADR-0047): uploads may carry data in a
    /// curated CRS and still land in one declared column CRS. A missing or
    /// equal source SRID is a pass-through, so the common 4326 case costs
    /// nothing and the codec stays free of algorithms.
    /// </summary>
    private static IReadOnlyList<FeatureBatch> ConvertIfNeeded(
        IReadOnlyList<FeatureBatch> pages, int? sourceSrid, int targetSrid, IServiceProvider services, CancellationToken token)
    {
        if (sourceSrid is not { } source || source == targetSrid)
        {
            return pages;
        }

        var transforms = services.GetRequiredService<ICoordinateTransforms>();
        var sourceCrs = $"EPSG:{source}";
        var targetCrs = $"EPSG:{targetSrid}";
        var converted = new List<FeatureBatch>(pages.Count);
        foreach (var page in pages)
        {
            var features = new Feature[page.Count];
            for (var index = 0; index < page.Count; index++)
            {
                features[index] = ConvertFeature(page[index], sourceCrs, targetCrs, transforms, token);
            }

            converted.Add(new FeatureBatch(page.Schema, features));
        }

        return converted;
    }

    private static Feature ConvertFeature(
        Feature feature, string source, string target, ICoordinateTransforms transforms, CancellationToken token)
    {
        var attributes = new AttributeValue[feature.Attributes.Count];
        for (var index = 0; index < attributes.Length; index++)
        {
            var value = feature.Attributes[index];
            attributes[index] = value.Kind == AttributeKind.Geometry && !value.IsNull
                ? AttributeValue.FromGeometry(transforms.Transform(value.GeometryValue, source, target, token))
                : value;
        }

        return new Feature(feature.Id, feature.Schema, attributes);
    }

    private static IngestFormat ParseFormat(IngestOptions ingest, string name)
    {
        var normalised = name.Trim().ToLowerInvariant();
        if (normalised.Length == 0)
        {
            throw SpatialException.BadArguments("The 'format' query parameter is required (geojson, ndjson or csv).");
        }

        if (!ingest.Formats.Contains(normalised, StringComparer.OrdinalIgnoreCase))
        {
            throw SpatialException.BadArguments(
                $"Format '{name}' is not enabled; accepted formats are {string.Join(", ", ingest.Formats)}.");
        }

        return normalised switch
        {
            "geojson" => IngestFormat.GeoJson,
            "ndjson" or "geojsonl" => IngestFormat.NewlineDelimitedGeoJson,
            "csv" => IngestFormat.Csv,
            _ => throw SpatialException.BadArguments($"Format '{name}' is not a supported ingest format."),
        };
    }

    private static IngestIdentity ParseIdentity(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return IngestIdentity.Auto;
        }

        return Enum.TryParse<IngestIdentity>(name, ignoreCase: true, out var identity)
            ? identity
            : throw SpatialException.BadArguments($"Unknown identity mode '{name}'; expected none, auto or source.");
    }

    private static string Required(IQueryCollection query, string key)
    {
        var value = query[key].ToString();
        return string.IsNullOrWhiteSpace(value)
            ? throw SpatialException.BadArguments($"The '{key}' query parameter is required.")
            : value;
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Returns a 401/403 result when the request is not authorised, otherwise null.</summary>
    private static IResult? Authorize(HttpContext context, AdminOptions admin)
    {
        var presented = PresentedToken(context);
        if (presented is null)
        {
            return ErrorMapper.Unauthorized("An admin token is required.");
        }

        return FixedTimeEquals(presented, admin.Token)
            ? null
            : ErrorMapper.Forbidden("The admin token is not valid.");
    }

    private static string? PresentedToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }

        var query = context.Request.Query["token"].ToString();
        return string.IsNullOrEmpty(query) ? null : query;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
