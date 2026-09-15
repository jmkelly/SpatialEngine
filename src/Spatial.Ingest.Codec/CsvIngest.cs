using System.Globalization;
using System.Text;
using Spatial.Core.Geometry;

namespace Spatial.Ingest.Codec;

/// <summary>
/// Parses RFC 4180-style CSV into raw records. The header names the attribute
/// columns; the geometry comes from a pair of x/y (or lon/lat, longitude/
/// latitude, easting/northing) columns, named explicitly or auto-detected. A
/// row with an empty coordinate cell yields a null geometry; a non-numeric
/// coordinate is a format failure.
/// </summary>
internal static class CsvIngest
{
    private static readonly (string X, string Y)[] GeometryColumnPairs =
    [
        ("x", "y"),
        ("lon", "lat"),
        ("longitude", "latitude"),
        ("easting", "northing"),
    ];

    /// <summary>Parses a CSV document into raw records.</summary>
    public static RawFeatureSet Read(Stream stream, DecodeOptions options, CoordinateReference crs)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var records = Parse(reader.ReadToEnd());
        if (records.Count == 0)
        {
            throw new IngestFormatException("The CSV document has no header row.");
        }

        var header = records[0];
        if (header.Count == 0 || header.All(string.IsNullOrWhiteSpace))
        {
            throw new IngestFormatException("The CSV document has no columns.");
        }

        var (xIndex, yIndex) = ResolveGeometryColumns(header, options);
        var set = new RawFeatureSet();
        for (var row = 1; row < records.Count; row++)
        {
            Add(set, header, records[row], xIndex, yIndex, crs, options);
        }

        return set;
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

    private static void Add(
        RawFeatureSet set,
        List<string> header,
        List<string> record,
        int xIndex,
        int yIndex,
        CoordinateReference crs,
        DecodeOptions options)
    {
        if (record.Count != header.Count)
        {
            throw new IngestFormatException(
                $"A CSV row has {record.Count} values but the header declares {header.Count} columns.");
        }

        var names = new List<string>(header.Count);
        var properties = new Dictionary<string, object?>(header.Count, StringComparer.Ordinal);
        for (var column = 0; column < header.Count; column++)
        {
            if (column == xIndex || column == yIndex)
            {
                continue;
            }

            names.Add(header[column]);
            properties[header[column]] = Scalar(record[column]);
        }

        var identity = options.IdentityField is { Length: > 0 } field && properties.TryGetValue(field, out var value)
            ? AsIdentity(value)
            : null;
        set.Add(identity, names, properties, ReadPoint(record, xIndex, yIndex, crs));
    }

    private static Point? ReadPoint(List<string> record, int xIndex, int yIndex, CoordinateReference crs)
    {
        var xText = record[xIndex];
        var yText = record[yIndex];
        if (xText.Length == 0 || yText.Length == 0)
        {
            return null;
        }

        if (double.TryParse(xText, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) is false
            || double.TryParse(yText, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) is false)
        {
            throw new IngestFormatException($"CSV coordinates must be numbers, got x='{xText}', y='{yText}'.");
        }

        return GeometryFactory.CreatePoint(x, y, crs);
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

    /// <summary>
    /// Splits CSV text into records, honouring double-quoted fields (including
    /// embedded commas, newlines and doubled quotes). A trailing newline does
    /// not produce an empty record.
    /// </summary>
    private static List<List<string>> Parse(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (quoted)
            {
                (i, quoted) = AppendQuoted(text, i, field);
                continue;
            }

            var c = text[i];
            if (c == '"')
            {
                quoted = true;
                continue;
            }

            if (c == ',')
            {
                record.Add(field.ToString());
                field.Clear();
                continue;
            }

            if (c == '\n')
            {
                record.Add(field.ToString());
                field.Clear();
                records.Add(record);
                record = [];
                continue;
            }

            if (c != '\r')
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }

        return records;
    }

    /// <summary>
    /// Consumes one character of a quoted field: a literal character, a doubled
    /// quote (which becomes one quote), or the closing quote. Returns the index
    /// to continue from and whether the field is still quoted.
    /// </summary>
    private static (int Index, bool Quoted) AppendQuoted(string text, int index, StringBuilder field)
    {
        var c = text[index];
        if (c != '"')
        {
            field.Append(c);
            return (index, true);
        }

        if (index + 1 < text.Length && text[index + 1] == '"')
        {
            field.Append('"');
            return (index + 1, true);
        }

        return (index, false);
    }
}
