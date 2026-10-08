using Spatial.Core.Features;
using Spatial.Core.Features.Ingest;
using Spatial.Core.Geometry;

namespace Spatial.Ingest.Codec;

/// <summary>
/// The entry point of the ingest codec (ADR-0041): decode an upload stream in a
/// declared <see cref="IngestFormat"/> into a core schema and canonical
/// <see cref="FeatureBatch"/> pages. Every failure is an
/// <see cref="IngestFormatException"/>, which the host and the Esri admin
/// projection map to <c>invalid.arguments</c>.
/// <para>
/// <see cref="Decode"/> materialises the whole upload, which is right for a
/// small one and wrong for a large one; <see cref="DecodeStreaming"/> yields
/// pages so a store can load each as it arrives under one transaction with
/// memory bounded by the page size rather than the file size.
/// </para>
/// </summary>
public static class DatasetDecoder
{
    /// <summary>Decodes a document into every page at once, reporting what it did.</summary>
    public static DecodedDataset Decode(
        Stream stream, IngestFormat format, DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        var decoded = DecodeAllAsync(stream, format, options, cancellationToken).GetAwaiter().GetResult();
        return new DecodedDataset(decoded.Schema, decoded.Pages, decoded.IdentityField, decoded.Report);
    }

    /// <summary>
    /// The buffered decode's engine, async so a caller already on the wire is
    /// not blocked on the stream's own reads.
    /// </summary>
    internal static async Task<DecodedDataset> DecodeAllAsync(
        Stream stream, IngestFormat format, DecodeOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new DecodeOptions();
        Validate(options);

        using var reader = Open(stream, format, options);
        var declared = await reader.InitialiseAsync(cancellationToken).ConfigureAwait(false);
        var crs = ResolveCrs(declared, options);

        var records = new List<RawFeature>();
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new List<IngestSkip>();
        long read = 0;
        long dropped = 0;
        var reprojected = false;

        while (await reader.ReadAsync(crs.SourceSrid, cancellationToken).ConfigureAwait(false) is { } record)
        {
            cancellationToken.ThrowIfCancellationRequested();
            read++;
            if (record.Skip is { } skip)
            {
                dropped++;
                if (skipped.Count < DecodeReport.MaxRecordedSkips)
                {
                    skipped.Add(skip);
                }

                continue;
            }

            var feature = record.Feature!;
            foreach (var name in feature.Properties.Keys)
            {
                if (seen.Add(name))
                {
                    names.Add(name);
                }
            }

            feature = feature with { Geometry = Reproject(feature.Geometry, crs, options, ref reprojected, cancellationToken) };
            records.Add(feature);
        }

        if (records.Count == 0)
        {
            throw new IngestFormatException("The document contains no features.");
        }

        var (schema, inferred) = IngestSchema.Infer(records, names, options.GeometryField);
        var pages = IngestSchema.Build(records, schema, options.BatchSize);
        var identity = Identity(options, schema);
        var report = new DecodeReport(
            read,
            records.Count,
            dropped,
            skipped,
            inferred,
            schema,
            crs with { Reprojected = reprojected },
            read,
            SchemaSampled: false);
        return new DecodedDataset(schema, pages, identity, report);
    }

    /// <summary>
    /// Opens a streaming decode: the schema is inferred from a bounded prefix
    /// and is therefore available before the first page, which is what lets a
    /// store create its table before it has read the upload.
    /// </summary>
    public static async Task<DecodeSession> DecodeStreamingAsync(
        Stream stream, IngestFormat format, DecodeOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= new DecodeOptions();
        Validate(options);
        cancellationToken.ThrowIfCancellationRequested();

        var reader = Open(stream, format, options);
        try
        {
            var sample = await ReadPrologueAsync(reader, options, cancellationToken).ConfigureAwait(false);
            var records = sample.Records;
            var names = sample.Names;
            var read = sample.Read;
            var (schema, inferred) = IngestSchema.Infer(records, names, options.GeometryField);
            var decoded = new DecodeSession(
                reader,
                options,
                schema,
                inferred,
                sample.Crs,
                records,
                sample.Skips,
                read,
                sample.Exhausted is false,
                Identity(options, schema));
            decoded.Prime(read, sample.Skipped);
            return decoded;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Reads the document preamble and the inference sample. The geometry
    /// columns of a CSV are only known once its header is read, and a store
    /// cannot create a table without a schema, so this part is deliberately
    /// eager and deliberately bounded.
    /// </summary>
    private static async Task<DecodeSample> ReadPrologueAsync(
        IRawRecordReader reader, DecodeOptions options, CancellationToken cancellationToken)
    {
        var declared = await reader.InitialiseAsync(cancellationToken).ConfigureAwait(false);
        var crs = ResolveCrs(declared, options);
        var limit = options.InferSampleSize ?? options.BatchSize;
        var records = new List<RawFeature>();
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skips = new List<IngestSkip>();
        long read = 0;
        long dropped = 0;
        var exhausted = false;

        while (records.Count < limit)
        {
            if (await reader.ReadAsync(crs.SourceSrid, cancellationToken).ConfigureAwait(false) is not { } record)
            {
                exhausted = true;
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            read++;
            if (record.Skip is { } skip)
            {
                dropped++;
                if (skips.Count < DecodeReport.MaxRecordedSkips)
                {
                    skips.Add(skip);
                }

                continue;
            }

            foreach (var name in record.Feature!.Properties.Keys)
            {
                if (seen.Add(name))
                {
                    names.Add(name);
                }
            }

            records.Add(record.Feature);
        }

        if (records.Count == 0)
        {
            throw new IngestFormatException("The document contains no features.");
        }

        return new DecodeSample(crs, records, names, read, dropped, exhausted, skips);
    }

    /// <summary>What the schema sample of a streaming decode read.</summary>
    private sealed record DecodeSample(
        IngestCrs Crs,
        List<RawFeature> Records,
        List<string> Names,
        long Read,
        long Skipped,
        bool Exhausted,
        List<IngestSkip> Skips);

    private static IRawRecordReader Open(Stream stream, IngestFormat format, DecodeOptions options) => format switch
    {
        IngestFormat.GeoJson => new GeoJsonRecordReader(stream, options.SkipMalformed),
        IngestFormat.NewlineDelimitedGeoJson => new NdGeoJsonRecordReader(stream, options.SkipMalformed),
        IngestFormat.Csv => new CsvRecordReader(stream, options, options.SkipMalformed),
        _ => throw new IngestFormatException($"Unsupported ingest format '{format}'."),
    };

    /// <summary>
    /// Resolves the decode's source CRS. A declaration the caller contradicts
    /// is a conflict, not a preference: one of the two is wrong and the engine
    /// cannot tell which, so guessing is how a dataset ends up in the wrong
    /// place with no error.
    /// </summary>
    private static IngestCrs ResolveCrs(string? declared, DecodeOptions options)
    {
        var declaredCode = declared is null ? (int?)null : IngestCrsName.Resolve(declared);
        if (declaredCode is { } code)
        {
            if (options.SourceSrid is { } asserted && asserted != code)
            {
                throw new IngestFormatException(
                    $"The document declares CRS '{declared}' (EPSG:{code}) but the decode was told the source is EPSG:{asserted}; the two must agree.");
            }
        }
        else if (declared is not null)
        {
            throw new IngestFormatException(
                $"The document declares CRS '{declared}', which is not an EPSG code; the engine only honours declared EPSG CRSs.");
        }

        var source = declaredCode ?? options.SourceSrid ?? options.Srid;
        var target = options.Srid;
        if (source != target && options.Reprojector is null)
        {
            throw new IngestFormatException(
                $"The document's CRS is EPSG:{source} but the decode targets EPSG:{target}; supply a reprojector or decode into EPSG:{source}.");
        }

        return new IngestCrs(source, declared, target, source != target);
    }

    private static IGeometry? Reproject(
        IGeometry? geometry, IngestCrs crs, DecodeOptions options, ref bool reprojected, CancellationToken cancellationToken)
    {
        if (geometry is null || crs.Reprojected is false)
        {
            return geometry;
        }

        cancellationToken.ThrowIfCancellationRequested();
        reprojected = true;
        return options.Reprojector!
            .Reproject(geometry, $"EPSG:{crs.SourceSrid}", $"EPSG:{crs.TargetSrid}", cancellationToken);
    }

    private static string? Identity(DecodeOptions options, FeatureSchema schema) =>
        options.IdentityField is { Length: > 0 } field && schema.IndexOf(field) >= 0 ? field : null;

    private static void Validate(DecodeOptions options)
    {
        CheckBatchSize(options);
        CheckInferSampleSize(options);
        CheckSourceSrid(options);
        CheckGeometryField(options);
    }

    private static void CheckBatchSize(DecodeOptions options)
    {
        if (options.BatchSize <= 0)
        {
            throw new IngestFormatException($"BatchSize must be positive, got {options.BatchSize}.");
        }
    }

    private static void CheckInferSampleSize(DecodeOptions options)
    {
        if (options.InferSampleSize is { } sample && sample <= 0)
        {
            throw new IngestFormatException($"InferSampleSize must be positive when set, got {sample}.");
        }
    }

    private static void CheckSourceSrid(DecodeOptions options)
    {
        if (options.SourceSrid is { } source && source <= 0)
        {
            throw new IngestFormatException($"SourceSrid must be a positive EPSG code when set, got {source}.");
        }
    }

    private static void CheckGeometryField(DecodeOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.GeometryField))
        {
            throw new IngestFormatException("GeometryField must be a non-empty name.");
        }
    }
}
