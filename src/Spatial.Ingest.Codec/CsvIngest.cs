using System.Globalization;
using System.Text;
using Spatial.Core.Features.Ingest;
using Spatial.Core.Geometry;

namespace Spatial.Ingest.Codec;

/// <summary>
/// A pull-based CSV record reader (RFC 4180 quoting, honoured across
/// embedded commas, newlines and doubled quotes). CSV declares no CRS of its
/// own, so the convention this codec adopts is a leading comment directive
/// before the header row — <c># crs=urn:ogc:def:crs:EPSG::27700</c> — which is
/// how every other line-oriented geospatial text format marks its reference
/// frame and costs a producer one line.
/// </summary>
internal sealed class CsvRecordReader : IRawRecordReader
{
    private const int WorkingSize = 64 * 1024;

    private static readonly (string X, string Y)[] GeometryColumnPairs =
    [
        ("x", "y"),
        ("lon", "lat"),
        ("longitude", "latitude"),
        ("easting", "northing"),
    ];

    private readonly StreamReader _reader;
    private readonly DecodeOptions _options;
    private readonly bool _skipMalformed;

    private List<string> _header = [];
    private List<string> _record = [];
    private readonly StringBuilder _field = new();
    private int _xIndex = -1;
    private int _yIndex = -1;
    private int _line;
    private int _position;
    private bool _initialised;

    public CsvRecordReader(Stream stream, DecodeOptions options, bool skipMalformed)
    {
        _reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _options = options;
        _skipMalformed = skipMalformed;
    }

    public async ValueTask<string?> InitialiseAsync(CancellationToken cancellationToken)
    {
        if (_initialised)
        {
            return null;
        }

        _initialised = true;
        string? crs = null;
        while (true)
        {
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new IngestFormatException("The CSV document has no header row.");
            }

            _line++;
            if (line.StartsWith('#'))
            {
                crs = Directive(line) ?? crs;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            _header = Split(line);
            break;
        }

        if (_header.Count == 0 || _header.All(string.IsNullOrWhiteSpace))
        {
            throw new IngestFormatException("The CSV document has no columns.");
        }

        (_xIndex, _yIndex) = ResolveGeometryColumns(_header, _options);
        return crs;
    }

    public async ValueTask<RawRecord?> ReadAsync(int sourceSrid, CancellationToken cancellationToken)
    {
        while (true)
        {
            var record = await NextRecordAsync(cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return null;
            }

            // A blank line is not a record, and a trailing newline is not one
            // either: both would otherwise be a ragged one-value row.
            if (record.Count == 1 && record[0].Length == 0)
            {
                continue;
            }

            // Skips report the line the row started on, which for CSV counts
            // the header and any preamble directive — the number an operator
            // sees in their editor.
            return Convert(record, _position, sourceSrid);
        }
    }

    public void Dispose() => _reader.Dispose();

    private RawRecord Convert(List<string> record, int position, int sourceSrid)
    {
        if (record.Count != _header.Count)
        {
            return Skip(
                position,
                IngestSkipReason.RecordMalformed,
                $"a row has {record.Count} values but the header declares {_header.Count} columns");
        }

        var names = new List<string>(_header.Count);
        var properties = new Dictionary<string, object?>(_header.Count, StringComparer.Ordinal);
        for (var column = 0; column < _header.Count; column++)
        {
            if (column == _xIndex || column == _yIndex)
            {
                continue;
            }

            names.Add(_header[column]);
            properties[_header[column]] = Scalar(record[column]);
        }

        var identity = _options.IdentityField is { Length: > 0 } field && properties.TryGetValue(field, out var value)
            ? AsIdentity(value)
            : null;

        if (TryReadPoint(record, sourceSrid, out var geometry, out var failure) is false)
        {
            return Skip(position, IngestSkipReason.GeometryInvalid, failure!);
        }

        return RawRecord.Parsed(position, new RawFeature(identity, properties, geometry));
    }

    private RawRecord Skip(int position, IngestSkipReason reason, string detail)
    {
        if (!_skipMalformed)
        {
            throw new IngestFormatException($"Row {position}: {GeoJsonFeatures.Describe(reason)} ({detail}).");
        }

        return RawRecord.Dropped(position, reason, detail);
    }

    /// <summary>
    /// The point a row's x/y pair decodes to. An empty cell is a null geometry,
    /// which is a fact about the row; a non-numeric cell is a malformed row.
    /// </summary>
    private bool TryReadPoint(
        List<string> record, int sourceSrid, out Point? geometry, out string? failure)
    {
        geometry = null;
        failure = null;
        var xText = record[_xIndex];
        var yText = record[_yIndex];
        if (xText.Length == 0 || yText.Length == 0)
        {
            return true;
        }

        if (double.TryParse(xText, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) is false
            || double.TryParse(yText, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) is false)
        {
            failure = $"coordinates must be numbers, got x='{xText}', y='{yText}'";
            return false;
        }

        geometry = GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(sourceSrid));
        return true;
    }

    /// <summary>
    /// Reads the next record by consuming physical lines until the quoting
    /// balances, splitting each line as it goes. A trailing newline does not
    /// produce an empty record.
    /// </summary>
    private async ValueTask<List<string>?> NextRecordAsync(CancellationToken cancellationToken)
    {
        _record.Clear();
        _field.Clear();
        _position = _line + 1;
        while (true)
        {
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                if (_record.Count == 0 && _field.Length == 0)
                {
                    return null;
                }

                _record.Add(_field.ToString());
                _field.Clear();
                var last = _record;
                _record = [];
                _line++;
                return last;
            }

            _line++;
            Consume(line);
            if (_inQuotes)
            {
                // A newline inside a quoted field belongs to the value.
                _field.Append('\n');
                continue;
            }

            var record = _record;
            _record = [];
            return record;
        }
    }

    private bool _inQuotes;

    /// <summary>Feeds one physical line through the RFC 4180 state machine.</summary>
    private void Consume(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (_inQuotes)
            {
                if (c != '"')
                {
                    _field.Append(c);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    _field.Append('"');
                    i++;
                }
                else
                {
                    _inQuotes = false;
                }

                continue;
            }

            if (c == '"')
            {
                _inQuotes = true;
            }
            else if (c == ',')
            {
                _record.Add(_field.ToString());
                _field.Clear();
            }
            else
            {
                _field.Append(c);
            }
        }

        if (_inQuotes is false)
        {
            _record.Add(_field.ToString());
            _field.Clear();
        }
    }

    /// <summary>Splits a whole line, for the header row.</summary>
    private static List<string> Split(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }

    /// <summary>The CRS named by a <c># crs=…</c> preamble directive, if any.</summary>
    private static string? Directive(string line)
    {
        var body = line.AsSpan(1).Trim();
        const string Key = "crs";
        if (body.Length <= Key.Length || body[..Key.Length].Equals(Key, StringComparison.OrdinalIgnoreCase) is false)
        {
            return null;
        }

        var rest = body[Key.Length..].TrimStart();
        if (rest.Length == 0)
        {
            return null;
        }

        rest = rest[1..].Trim();
        return rest.Length == 0 ? null : rest.ToString();
    }

    private static (int X, int Y) ResolveGeometryColumns(List<string> header, DecodeOptions options)
    {
        var x = IndexOf(header, options.XField);
        var y = IndexOf(header, options.YField);
        if (x >= 0 && y >= 0)
        {
            return (x, y);
        }

        if (string.IsNullOrWhiteSpace(options.XField) is false || string.IsNullOrWhiteSpace(options.YField) is false)
        {
            throw new IngestFormatException("Both XField and YField must name an existing CSV column.");
        }

        foreach (var pair in GeometryColumnPairs)
        {
            var pairX = IndexOf(header, pair.X);
            var pairY = IndexOf(header, pair.Y);
            if (pairX >= 0 && pairY >= 0)
            {
                return (pairX, pairY);
            }
        }

        throw new IngestFormatException(
            "The CSV document has no recognised geometry columns; supply XField and YField (for example x and y, or lon and lat).");
    }

    private static object? Scalar(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        if (value is "true" or "false")
        {
            return value == "true";
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : value;
    }

    private static string? AsIdentity(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is long integer)
        {
            return integer.ToString(CultureInfo.InvariantCulture);
        }

        if (value is double number)
        {
            return number.ToString("R", CultureInfo.InvariantCulture);
        }

        return System.Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static int IndexOf(List<string> header, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return -1;
        }

        for (var i = 0; i < header.Count; i++)
        {
            if (string.Equals(header[i].Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
