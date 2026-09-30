using Microsoft.AspNetCore.Http;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Ingest.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Esri <c>/uploads</c> projection (ADR-0041 §5): a multipart upload is
/// read under the configured byte cap, decoded through the ingest codec for the
/// requested format/identity and ingested into a store. The result is staged
/// (<see cref="EsriUploadStaging"/>) for a later publish, never published
/// implicitly.
/// </summary>
internal static class EsriAdminUploads
{
    public static async Task<IResult> UploadAsync(
        EsriAdminOptions options,
        EsriUploadStaging staging,
        HttpContext context,
        IStoreRegistry stores,
        ICoordinateTransforms transforms,
        CancellationToken token)
    {
        var query = context.Request.Query;
        var store = EsriAdminRequest.Query(query, "store") ?? "memory";
        var dataset = EsriAdminRequest.Query(query, "dataset") ?? throw GeoServicesErrors.Invalid("A 'dataset' query parameter is required.");
        var format = ParseFormat(EsriAdminRequest.Query(query, "format") ?? "geojson");
        var srid = ParseSrid(EsriAdminRequest.Query(query, "srid"));
        var identityField = EsriAdminRequest.Query(query, "identityField");
        var identity = IdentityOf(EsriAdminRequest.Query(query, "identity"));
        var target = stores.Ingest(store)
            ?? throw GeoServicesErrors.Invalid($"Store '{store}' does not support ingest.");

        await using var body = await ReadUploadAsync(context.Request, options.MaxBytes);

        // The declared source CRS is honoured by the decode, and streamed into
        // the store when it can take pages; the projection reports the item id,
        // so the decode report is staged with the outcome rather than returned.
        var request = new IngestRequest(dataset, srid, identity, identityField);
        if (target is IDatasetIngestStream streaming)
        {
            await using var session = await DatasetDecoder.DecodeStreamingAsync(
                body, format, Options(options, srid, identityField, transforms), token);
            var streamed = await streaming
                .IngestStreamAsync(
                    request,
                    session.Schema,
                    IngestPageCap.Apply(session.Pages, options.MaxFeatures, GeoServicesErrors.Invalid, token),
                    token)
                .ConfigureAwait(false);
            return EsriAdminResponses.Upload(
                staging.Stage(streamed with { Report = await session.Report.ConfigureAwait(false) }, store));
        }

        var decoded = DatasetDecoder.Decode(body, format, Options(options, srid, identityField, transforms), token);
        var features = decoded.Pages.Sum(page => (long)page.Count);
        if (features > options.MaxFeatures)
        {
            throw GeoServicesErrors.Invalid(IngestPageCap.Exceeded(options.MaxFeatures));
        }

        var outcome = await target.IngestAsync(request, decoded.Pages, token);
        return EsriAdminResponses.Upload(staging.Stage(outcome with { Report = decoded.Report }, store));
    }

    /// <summary>
    /// The decode options the Esri projection shares with the neutral path.
    /// </summary>
    private static DecodeOptions Options(
        EsriAdminOptions options, int srid, string? identityField, ICoordinateTransforms transforms) => new()
        {
            Srid = srid,
            BatchSize = options.BatchSize,
            IdentityField = identityField,
            SkipMalformed = options.SkipMalformed,
            Reprojector = new EsriReprojection(transforms),
        };

    /// <summary>The engine's transform service, adapted onto the codec's seam.</summary>
    private sealed class EsriReprojection(ICoordinateTransforms transforms) : IIngestReprojection
    {
        public Spatial.Core.Geometry.IGeometry Reproject(
            Spatial.Core.Geometry.IGeometry geometry, string source, string target, CancellationToken cancellationToken = default) =>
            transforms.Transform(geometry, source, target, cancellationToken);
    }

    private static async Task<MemoryStream> ReadUploadAsync(HttpRequest request, long maxBytes)
    {
        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or BadHttpRequestException)
        {
            throw GeoServicesErrors.Invalid(
                $"The multipart upload is malformed and the 'file' part could not be read: {exception.Message}");
        }

        var file = form.Files.Count > 0
            ? form.Files[0]
            : throw GeoServicesErrors.Invalid("The multipart upload carries no file part.");
        if (file.Length > maxBytes)
        {
            throw GeoServicesErrors.Invalid($"The upload is {file.Length} bytes, above the configured maximum of {maxBytes}.");
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
        ["source"] = IngestIdentity.Source,
    };

    private static IngestFormat ParseFormat(string name) =>
        Formats.TryGetValue(name, out var format)
            ? format
            : throw GeoServicesErrors.Invalid($"Format '{name}' is not a supported ingest format.");

    private static int ParseSrid(string? value) =>
        int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var srid) && srid > 0
            ? srid
            : throw GeoServicesErrors.Invalid($"The 'srid' query parameter must be a positive integer, got '{value}'.");

    private static IngestIdentity IdentityOf(string? name) =>
        string.IsNullOrEmpty(name)
            ? IngestIdentity.Auto
            : Identities.TryGetValue(name, out var identity)
                ? identity
                : throw GeoServicesErrors.Invalid(string.Equals(name, "none", StringComparison.OrdinalIgnoreCase)
                    ? "Identity 'none' is not supported: every ingested dataset carries an identity column, so it is editable and lookup-able. Use 'auto' for a store-assigned key or 'source' with an identityField."
                    : $"Unknown identity mode '{name}'; expected auto or source.");


}
