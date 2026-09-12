using Spatial.Core.Geometry;

namespace Spatial.Interop.Ingest;

/// <summary>
/// The single entry point of the ingest codec (ADR-0041): decode an upload
/// stream in a declared <see cref="IngestFormat"/> into a core schema and
/// canonical <see cref="DecodedDataset"/> pages. Every failure is an
/// <see cref="IngestFormatException"/>, which the host and the Esri admin
/// projection map to <c>invalid.arguments</c>.
/// </summary>
public static class DatasetDecoder
{
    /// <summary>Decodes a document; the geometry field is always appended to the inferred schema.</summary>
    public static DecodedDataset Decode(Stream stream, IngestFormat format, DecodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new DecodeOptions();
        Validate(options);

        var crs = CoordinateReference.Epsg(options.Srid);
        var set = Read(stream, format, options, crs);
        if (set.Features.Count == 0)
        {
            throw new IngestFormatException("The document contains no features.");
        }

        var schema = IngestSchema.Infer(set, options.GeometryField);
        var pages = IngestSchema.Build(set, schema, options.BatchSize);
        var identity = options.IdentityField is { Length: > 0 } field && schema.IndexOf(field) >= 0 ? field : null;
        return new DecodedDataset(schema, pages, identity);
    }

    private static RawFeatureSet Read(Stream stream, IngestFormat format, DecodeOptions options, CoordinateReference crs)
    {
        if (format == IngestFormat.GeoJson)
        {
            return GeoJsonIngest.ReadCollection(stream, crs);
        }

        if (format == IngestFormat.NewlineDelimitedGeoJson)
        {
            return GeoJsonIngest.ReadDelimited(stream, crs);
        }

        if (format == IngestFormat.Csv)
        {
            return CsvIngest.Read(stream, options, crs);
        }

        throw new IngestFormatException($"Unsupported ingest format '{format}'.");
    }

    private static void Validate(DecodeOptions options)
    {
        if (options.BatchSize <= 0)
        {
            throw new IngestFormatException($"BatchSize must be positive, got {options.BatchSize}.");
        }

        if (string.IsNullOrWhiteSpace(options.GeometryField))
        {
            throw new IngestFormatException("GeometryField must be a non-empty name.");
        }
    }
}
