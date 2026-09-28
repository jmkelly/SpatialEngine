using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Ingest.Codec;

namespace Spatial.Host.Api;

/// <summary>
/// The store-facing half of the neutral ingest path (ADR-0041 §3): decode and
/// load. Shared by the upload route (<see cref="AdminEndpoints"/>) and the
/// development seed endpoint (<see cref="SeedEndpoints"/>), so both enforce the
/// same format allowlist, feature cap and source-to-target reprojection.
/// <para>
/// Reprojection belongs to the decode, not to a second pass over the pages
/// (ADR-0041 §4): the codec resolves the source CRS from the document's own
/// declaration, transforms as it decodes, and reports both. A second pass
/// could only be told the source CRS by the caller, which is exactly the
/// assumption that let a declared CRS be ignored.
/// </para>
/// </summary>
internal static class IngestPipeline
{
    /// <summary>Adapts the engine's transform service onto the codec's seam.</summary>
    public static IIngestReprojection Reprojection(ICoordinateTransforms transforms)
    {
        ArgumentNullException.ThrowIfNull(transforms);
        return new TransformReprojection(transforms);
    }

    private sealed class TransformReprojection(ICoordinateTransforms transforms) : IIngestReprojection
    {
        public IGeometry Reproject(IGeometry geometry, string source, string target, CancellationToken cancellationToken = default) =>
            transforms.Transform(geometry, source, target, cancellationToken);
    }

    /// <summary>
    /// Decodes an upload body under the feature cap and loads it, streaming
    /// when the store can take pages as they arrive. The returned outcome
    /// carries the decode report, so the caller learns what was read, what was
    /// dropped and why, and what happened to the CRS.
    /// </summary>
    public static async Task<IngestOutcome> LoadAsync(
        Stream body,
        IngestFormat format,
        string dataset,
        int targetSrid,
        int? sourceSrid,
        IDatasetIngest target,
        IDatasetIngestStream? streaming,
        IngestOptions ingest,
        IngestIdentity identity,
        string? identityField,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var options = new DecodeOptions
        {
            Srid = targetSrid,
            SourceSrid = sourceSrid,
            Reprojector = Reprojection(transforms),
            BatchSize = ingest.BatchSize,
            IdentityField = identityField,
            SkipMalformed = ingest.SkipMalformed,
        };
        var request = new IngestRequest(dataset, targetSrid, identity, identityField);

        if (streaming is null)
        {
            var decoded = DatasetDecoder.Decode(body, format, options, cancellationToken);
            var features = decoded.Pages.Sum(page => (long)page.Count);
            if (features > ingest.MaxFeatures)
            {
                throw SpatialException.BadArguments(IngestPageCap.Exceeded(ingest.MaxFeatures));
            }
            var loaded = await target.IngestAsync(request, decoded.Pages, cancellationToken).ConfigureAwait(false);
            return loaded with { Report = decoded.Report };
        }

        await using var session = await DatasetDecoder
            .DecodeStreamingAsync(body, format, options, cancellationToken)
            .ConfigureAwait(false);
        var streamed = await streaming
            .IngestStreamAsync(
                request,
                session.Schema,
                UnderCap(session.Pages, ingest, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        return streamed with { Report = await session.Report.ConfigureAwait(false) };
    }

    /// <summary>
    /// Fails the decode once it has read more features than the cap allows.
    /// The buffered path checks the count after the fact; a streamed one has to
    /// check while reading, or the cap is not a cap. The Esri projection
    /// enforces the same cap through the same helper (ADR-0083).
    /// </summary>
    private static IAsyncEnumerable<FeatureBatch> UnderCap(
        IAsyncEnumerable<FeatureBatch> pages,
        IngestOptions ingest,
        CancellationToken cancellationToken) =>
        IngestPageCap.Apply(pages, ingest.MaxFeatures, SpatialException.BadArguments, cancellationToken);

    public static IngestFormat ParseFormat(IngestOptions ingest, string name)
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

    public static IngestIdentity ParseIdentity(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return IngestIdentity.Auto;
        }

        return Enum.TryParse<IngestIdentity>(name, ignoreCase: true, out var identity)
            ? identity
            : throw SpatialException.BadArguments($"Unknown identity mode '{name}'; expected none, auto or source.");
    }

    public static int ParseSrid(string value) =>
        int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var srid) && srid > 0
            ? srid
            : throw SpatialException.BadArguments($"The 'srid' query parameter must be a positive integer, got '{value}'.");

    public static int? ParseOptionalSrid(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var srid) && srid > 0
            ? srid
            : throw SpatialException.BadArguments($"The 'sourceSrid' query parameter must be a positive integer, got '{value}'.");
    }
}
