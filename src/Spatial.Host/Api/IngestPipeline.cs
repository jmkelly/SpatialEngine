using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Ingest.Codec;

namespace Spatial.Host.Api;

/// <summary>
/// The store-facing half of the neutral ingest path (ADR-0041 §3): decode,
/// cap, reproject and load. Shared by the upload route
/// (<see cref="AdminEndpoints"/>) and the development seed endpoint
/// (<see cref="SeedEndpoints"/>), so both enforce the same format allowlist,
/// feature cap and source-to-target reprojection.
/// </summary>
internal static class IngestPipeline
{
    /// <summary>Decodes an upload body under the feature cap.</summary>
    public static IReadOnlyList<FeatureBatch> DecodePages(
        Stream body, IngestFormat format, int srid, IngestOptions ingest, string? identityField)
    {
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

        return decoded.Pages;
    }

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

    /// <summary>
    /// Reprojects decoded pages from the source CRS to the target SRID through
    /// the engine's transform service (ADR-0047): uploads may carry data in a
    /// curated CRS and still land in one declared column CRS. A missing or
    /// equal source SRID is a pass-through, so the common 4326 case costs
    /// nothing and the codec stays free of algorithms.
    /// </summary>
    public static IReadOnlyList<FeatureBatch> ConvertIfNeeded(
        IReadOnlyList<FeatureBatch> pages, int? sourceSrid, int targetSrid, ICoordinateTransforms transforms, CancellationToken token)
    {
        if (sourceSrid is not { } source || source == targetSrid)
        {
            return pages;
        }

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
}
