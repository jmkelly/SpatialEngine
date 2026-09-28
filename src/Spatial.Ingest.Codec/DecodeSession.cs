using System.Runtime.CompilerServices;
using Spatial.Core.Features;
using Spatial.Core.Features.Ingest;
using Spatial.Core.Geometry;

namespace Spatial.Ingest.Codec;

/// <summary>
/// One decode in flight (ADR-0041 §4). The schema is known up front — a store
/// creates its table from it before the first page arrives — and the report
/// completes when the page sequence is drained, because a report that counted
/// records cannot exist before the last one has been read.
/// </summary>
public sealed class DecodeSession : IAsyncDisposable
{
    private readonly IRawRecordReader _reader;
    private readonly DecodeOptions _options;
    private readonly IngestCrs _crs;
    private readonly IReadOnlyList<RawFeature> _primed;
    private readonly bool _schemaSampled;
    private readonly List<IngestSkip> _skips;
    private readonly TaskCompletionSource<DecodeReport> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long _read;
    private long _emitted;
    private long _skipped;
    private bool _reprojected;
    private bool _reported;

    internal DecodeSession(
        IRawRecordReader reader,
        DecodeOptions options,
        FeatureSchema schema,
        IReadOnlyList<InferredField> inferred,
        IngestCrs crs,
        IReadOnlyList<RawFeature> primed,
        List<IngestSkip> primedSkips,
        long sampled,
        bool schemaSampled,
        string? identityField)
    {
        _reader = reader;
        _options = options;
        _crs = crs;
        _primed = primed;
        _skips = primedSkips;
        _schemaSampled = schemaSampled;
        Schema = schema;
        Inferred = inferred;
        IdentityField = identityField;
        Crs = crs;
        Sampled = sampled;
    }

    /// <summary>The inferred schema, known before the first page is awaited.</summary>
    public FeatureSchema Schema { get; }

    /// <summary>The evidence behind each inferred attribute field.</summary>
    public IReadOnlyList<InferredField> Inferred { get; }

    /// <summary>The schema field that can serve as the identity, when the source supplied one.</summary>
    public string? IdentityField { get; }

    /// <summary>What the decode resolved the source CRS to, before reading any feature.</summary>
    public IngestCrs Crs { get; }

    /// <summary>How many records the schema was inferred from.</summary>
    public long Sampled { get; }

    /// <summary>
    /// Completes with the report once <see cref="Pages"/> has been drained, or
    /// as soon as the stream is disposed. A load that failed or was cancelled
    /// gets the last known counts rather than a report that never arrives.
    /// </summary>
    public Task<DecodeReport> Report => _completed.Task;

    /// <summary>The pages, in document order, at most <see cref="DecodeOptions.BatchSize"/> each.</summary>
    public IAsyncEnumerable<FeatureBatch> Pages => StreamAsync();

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        Complete();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Seeds the counters with what the schema sample already read, including
    /// the records it dropped: a record skipped before the session existed is
    /// still a record the report has to account for.
    /// </summary>
    internal void Prime(long read, long skipped)
    {
        _read = read;
        _skipped = skipped;
    }

    private async IAsyncEnumerable<FeatureBatch> StreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new List<Feature>(_options.BatchSize);
        var index = 0;
        try
        {
            // The records the schema was inferred from are read before the
            // session existed, so they are emitted first rather than dropped:
            // a sample is a way to learn the schema, not a way to lose rows.
            foreach (var primed in _primed)
            {
                if (Add(primed, ref buffer, ref index, cancellationToken) is { } first)
                {
                    _emitted += buffer.Count;
                    yield return first;
                    buffer.Clear();
                }
            }

            while (true)
            {
                var record = await _reader.ReadAsync(_crs.SourceSrid, cancellationToken).ConfigureAwait(false);
                if (record is null)
                {
                    break;
                }

                _read++;
                if (record.Value.Skip is { } skip)
                {
                    Skip(skip);
                    continue;
                }

                if (Add(record.Value.Feature!, ref buffer, ref index, cancellationToken) is { } page)
                {
                    _emitted += buffer.Count;
                    yield return page;
                    buffer.Clear();
                }
            }

            if (buffer.Count > 0)
            {
                _emitted += buffer.Count;
                yield return new FeatureBatch(Schema, [.. buffer]);
            }
        }
        finally
        {
            Complete();
        }
    }

    /// <summary>
    /// Reprojects and builds one record into the page buffer, returning a full
    /// page when the record filled it.
    /// </summary>
    private FeatureBatch? Add(RawFeature raw, ref List<Feature> buffer, ref int index, CancellationToken cancellationToken)
    {
        if (_crs.Reprojected && raw.Geometry is not null)
        {
            raw = raw with
            {
                Geometry = _options.Reprojector!.Reproject(
                    raw.Geometry, $"EPSG:{_crs.SourceSrid}", $"EPSG:{_crs.TargetSrid}", cancellationToken),
            };
            _reprojected = true;
        }

        if (IngestSchema.TryBuildFeature(raw, Schema, index, out var feature, out var unexpected) is false)
        {
            Skip(new IngestSkip(index + 1, IngestSkipReason.FieldNotInferred, $"no field for '{unexpected}'"));
            return null;
        }

        index++;
        buffer.Add(feature);
        return buffer.Count < _options.BatchSize ? null : new FeatureBatch(Schema, [.. buffer]);
    }

    private void Skip(IngestSkip skip)
    {
        _skipped++;
        if (_skips.Count < DecodeReport.MaxRecordedSkips)
        {
            _skips.Add(skip);
        }
    }

    private void Complete()
    {
        if (_reported)
        {
            return;
        }

        _reported = true;
        _completed.TrySetResult(new DecodeReport(
            _read,
            _emitted,
            _skipped,
            _skips,
            Inferred,
            Schema,
            _crs with { Reprojected = _reprojected },
            Sampled,
            _schemaSampled));
    }
}
