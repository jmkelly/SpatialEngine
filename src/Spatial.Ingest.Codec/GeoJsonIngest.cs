using System.Globalization;
using System.Text;
using System.Text.Json;
using Spatial.Core.Features.Ingest;
using Spatial.Core.Geometry;

namespace Spatial.Ingest.Codec;

/// <summary>
/// A pull-based GeoJSON record reader: a <c>FeatureCollection</c> is walked one
/// feature at a time out of a <see cref="Utf8JsonStream"/>, so the document is
/// never in memory in full. The root's <c>crs</c> member is read in the
/// preamble, before the first geometry is stamped, which is what lets a
/// declared CRS be honoured rather than ignored.
/// </summary>
internal sealed class GeoJsonRecordReader : IRawRecordReader
{
    private enum Phase
    {
        /// <summary>Walking the root object's members, looking for the features array.</summary>
        Preamble,

        /// <summary>Inside the features array.</summary>
        Features,

        /// <summary>The features array is closed; only the root's tail remains.</summary>
        Tail,

        Done,
    }

    private readonly Utf8JsonStream _json;
    private readonly bool _skipMalformed;

    /// <summary>
    /// A bare <c>Feature</c> root's members, held as documents because the
    /// feature is read member by member and its elements must outlive the read
    /// that produced them. A single feature is bounded by the feature.
    /// </summary>
    private readonly List<KeyValuePair<string, JsonDocument>> _root = [];

    private bool _bareFeature;

    private Phase _phase = Phase.Preamble;
    private bool _arrayStarted;
    private int _position;
    private string? _declaredCrs;
    private bool _initialised;

    public GeoJsonRecordReader(Stream stream, bool skipMalformed)
    {
        _json = new Utf8JsonStream(stream);
        _skipMalformed = skipMalformed;
    }

    public async ValueTask<string?> InitialiseAsync(CancellationToken cancellationToken)
    {
        if (_initialised)
        {
            return _declaredCrs;
        }

        _initialised = true;
        if (!await _json.ReadAsync(cancellationToken).ConfigureAwait(false) || _json.TokenType != JsonTokenType.StartObject)
        {
            throw new IngestFormatException("The document is not a JSON object.");
        }

        var type = await ReadPreambleAsync(cancellationToken).ConfigureAwait(false);
        if (type is null)
        {
            throw new IngestFormatException("The document is not a GeoJSON Feature or FeatureCollection.");
        }

        if (type != "Feature")
        {
            return _declaredCrs;
        }

        _position = 1;
        _bareFeature = true;
        _phase = Phase.Done;
        return _declaredCrs;
    }

    public async ValueTask<RawRecord?> ReadAsync(int sourceSrid, CancellationToken cancellationToken)
    {
        if (_bareFeature)
        {
            _bareFeature = false;
            var builder = new GeoJsonFeatures.Builder();
            foreach (var member in _root)
            {
                builder.Add(member.Key, member.Value.RootElement);
            }

            _root.Clear();
            return Convert(builder, _position, sourceSrid, "Feature");
        }

        return _phase switch
        {
            Phase.Preamble => await EnterFeaturesAsync(sourceSrid, cancellationToken).ConfigureAwait(false),
            Phase.Features => await NextFeatureAsync(sourceSrid, cancellationToken).ConfigureAwait(false),
            Phase.Tail => await CloseAsync(sourceSrid, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    public void Dispose()
    {
        foreach (var member in _root)
        {
            member.Value.Dispose();
        }

        _root.Clear();
        _json.Dispose();
    }

    /// <summary>
    /// Walks the root object's members, taking the type and the <c>crs</c>
    /// declaration and stopping on the <c>features</c> member. Everything the
    /// preamble reads is bounded by the document's header, not its features; a
    /// bare <c>Feature</c> root keeps its members because it is itself the only
    /// record there is.
    /// </summary>
    private async ValueTask<string?> ReadPreambleAsync(CancellationToken cancellationToken)
    {
        string? type = null;
        while (await _json.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_json.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (_json.TokenType != JsonTokenType.PropertyName)
            {
                throw new IngestFormatException("A GeoJSON document's members must be named.");
            }

            var name = _json.ValueText ?? string.Empty;
            if (name == "features" && type != "Feature")
            {
                // A collection's features may appear before its type, so an
                // undeclared type is a collection rather than an error.
                return type ?? "FeatureCollection";
            }

            if (type == "Feature")
            {
                if (name == "features")
                {
                    throw new IngestFormatException("A GeoJSON Feature has no 'features' member.");
                }
            }
            else if (name == "features")
            {
                return type ?? "FeatureCollection";
            }

            if (name == "type" && type is null)
            {
                if (await _json.ReadAsync(cancellationToken).ConfigureAwait(false) is false)
                {
                    break;
                }

                if (_json.TokenType == JsonTokenType.String)
                {
                    type = _json.ValueText;
                    continue;
                }
            }

            var value = await _json.ReadValueAsync(cancellationToken).ConfigureAwait(false);
            if (name == "crs")
            {
                _declaredCrs = NameOf(value.RootElement)
                    ?? throw new IngestFormatException("The document's 'crs' member has no 'name' property.");
            }

            // Ownership of the document moves into the preamble, which either
            // builds a bare Feature from it or releases it on disposal.
            _root.Add(new KeyValuePair<string, JsonDocument>(name, value));
        }

        return type;
    }

    private async ValueTask<RawRecord?> EnterFeaturesAsync(int sourceSrid, CancellationToken cancellationToken)
    {
        _phase = Phase.Features;
        if (!await _json.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _phase = Phase.Done;
            return null;
        }

        if (_json.TokenType != JsonTokenType.StartArray)
        {
            throw new IngestFormatException("A GeoJSON FeatureCollection needs an array 'features'.");
        }

        return await NextFeatureAsync(sourceSrid, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RawRecord?> NextFeatureAsync(int sourceSrid, CancellationToken cancellationToken)
    {
        if (_arrayStarted is false)
        {
            _arrayStarted = true;
            return await NextFeatureAsync(sourceSrid, cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            var token = await _json.PeekAsync(cancellationToken).ConfigureAwait(false);
            if (token == JsonTokenType.None)
            {
                _phase = Phase.Done;
                return null;
            }

            if (token == JsonTokenType.EndArray)
            {
                // Peeking does not consume, and the root's tail starts after
                // this bracket, so close it here.
                await _json.ReadAsync(cancellationToken).ConfigureAwait(false);
                _phase = Phase.Tail;
                return null;
            }

            if (token != JsonTokenType.StartObject)
            {
                throw new IngestFormatException("A GeoJSON 'features' array must hold objects.");
            }

            _position++;
            using var document = await _json.ReadValueAsync(cancellationToken).ConfigureAwait(false);
            var builder = new GeoJsonFeatures.Builder();
            foreach (var member in document.RootElement.EnumerateObject())
            {
                builder.Add(member.Name, member.Value);
            }

            var record = Convert(builder, _position, sourceSrid, "Feature");
            if (record is not null)
            {
                return record;
            }
        }
    }

    /// <summary>
    /// Consumes what is left of the root object, which is where a misplaced
    /// <c>crs</c> surfaces: coordinates are already stamped by then, so a
    /// declaration that disagrees is a failure rather than a retroactive
    /// reprojection nobody asked for.
    /// </summary>
    private async ValueTask<RawRecord?> CloseAsync(int sourceSrid, CancellationToken cancellationToken)
    {
        var closed = false;
        while (await _json.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_json.TokenType == JsonTokenType.EndObject)
            {
                closed = true;
                break;
            }

            if (_json.TokenType != JsonTokenType.PropertyName)
            {
                throw new IngestFormatException("A GeoJSON document's members must be named.");
            }

            var isCrs = _json.ValueText == "crs";
            using var value = await _json.ReadValueAsync(cancellationToken).ConfigureAwait(false);
            if (isCrs is false)
            {
                continue;
            }

            var declared = NameOf(value.RootElement)
                ?? throw new IngestFormatException("The document's 'crs' member has no 'name' property.");
            var code = IngestCrsName.Resolve(declared);
            if (code != sourceSrid)
            {
                throw new IngestFormatException(
                    $"The document declares CRS '{declared}' (EPSG:{code}) after its 'features', which were read as EPSG:{sourceSrid}; a declared CRS must precede the features it describes.");
            }

            _declaredCrs ??= declared;
        }

        if (closed is false)
        {
            throw new IngestFormatException("The document ended in the middle of a GeoJSON object.");
        }

        if (await _json.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new IngestFormatException("The document has trailing content after the GeoJSON object.");
        }

        _phase = Phase.Done;
        return null;
    }

    /// <summary>Converts one feature, or the typed reason it was dropped.</summary>
    private RawRecord? Convert(GeoJsonFeatures.Builder builder, int position, int sourceSrid, string what)
    {
        if (builder.Finish(position, sourceSrid, out var feature, out var skip))
        {
            return RawRecord.Parsed(position, feature!);
        }

        if (!_skipMalformed)
        {
            throw new IngestFormatException(
                $"{what} {position}: {GeoJsonFeatures.Describe(skip!.Reason)} ({skip.Detail}).");
        }

        return RawRecord.Dropped(position, skip!.Reason, skip.Detail);
    }

    /// <summary>The <c>crs</c> member's name, in the GeoJSON 2008 shape.</summary>
    internal static string? CrsOf(JsonElement feature) =>
        feature.ValueKind == JsonValueKind.Object && feature.TryGetProperty("crs", out var crs)
            ? NameOf(crs)
            : null;

    /// <summary>The CRS name a <c>crs</c> member carries, or null when it carries none.</summary>
    internal static string? NameOf(JsonElement crs) =>
        crs.ValueKind == JsonValueKind.Object
        && crs.TryGetProperty("properties", out var properties)
        && properties.ValueKind == JsonValueKind.Object
        && properties.TryGetProperty("name", out var name)
            ? name.GetString()
            : null;
}

/// <summary>
/// A pull-based newline-delimited GeoJSON reader: one <c>Feature</c> per
/// non-blank line. The first record's <c>crs</c> member is the document's
/// declaration; a later record naming a different CRS is a conflict rather than
/// a per-feature override, because one dataset cannot mix coordinate systems.
/// </summary>
internal sealed class NdGeoJsonRecordReader : IRawRecordReader
{
    private readonly StreamReader _reader;
    private readonly bool _skipMalformed;

    private JsonElement? _pending;
    private IngestSkip? _pendingSkip;
    private string? _declaredCrs;
    private int _lineNumber;
    private int _position;
    private bool _initialised;
    private bool _drained;

    public NdGeoJsonRecordReader(Stream stream, bool skipMalformed)
    {
        _reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        _skipMalformed = skipMalformed;
    }

    public async ValueTask<string?> InitialiseAsync(CancellationToken cancellationToken)
    {
        if (_initialised)
        {
            return _declaredCrs;
        }

        _initialised = true;
        // The declaration lives inside the first record, so the preamble has to
        // hold that one record: bounded by a line, not by the upload.
        while (true)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            _position++;
            if (TryParse(line, _position, out var document))
            {
                _declaredCrs = GeoJsonRecordReader.CrsOf(document!.RootElement);
                _pending = document.RootElement.Clone();
                document.Dispose();
                return _declaredCrs;
            }

            if (!_skipMalformed)
            {
                throw new IngestFormatException($"Line {_lineNumber} is not valid JSON: {_parseFailure}");
            }

            return null;
        }
    }

    private string? _parseFailure;

    public async ValueTask<RawRecord?> ReadAsync(int sourceSrid, CancellationToken cancellationToken)
    {
        if (_pending is { } pending)
        {
            _pending = null;
            return Convert(pending, _position, sourceSrid);
        }

        if (_pendingSkip is { } skipped)
        {
            _pendingSkip = null;
            return RawRecord.Dropped(skipped.Record, skipped.Reason, skipped.Detail);
        }

        while (!_drained)
        {
            var line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            _position++;
            if (TryParse(line, _position, out var document) is false)
            {
                var failed = _pendingSkip!;
                _pendingSkip = null;
                if (!_skipMalformed)
                {
                    throw new IngestFormatException($"Line {_lineNumber} is not valid JSON: {failed.Detail}");
                }

                return RawRecord.Dropped(_position, failed.Reason, failed.Detail);
            }

            using (document)
            {
                var root = document!.RootElement;
                if (GeoJsonRecordReader.CrsOf(root) is { } declared)
                {
                    CheckDeclared(declared, _position, sourceSrid);
                }

                return Convert(root, _position, sourceSrid);
            }
        }

        return null;
    }

    public void Dispose() => _reader.Dispose();

    private void CheckDeclared(string declared, int position, int sourceSrid)
    {
        var code = IngestCrsName.Resolve(declared);
        if (code != sourceSrid)
        {
            var line = position == 1 ? "Line 1" : $"Line {position}";
            var conflict = _declaredCrs is not null && _declaredCrs != declared
                ? $" but the document declared '{_declaredCrs}' on its first record"
                : string.Empty;
            throw new IngestFormatException(
                $"{line} declares CRS '{declared}' (EPSG:{code}){conflict}; the decode is reading EPSG:{sourceSrid} and one upload cannot mix coordinate systems.");
        }

        _declaredCrs ??= declared;
    }

    private RawRecord? Convert(JsonElement element, int position, int sourceSrid)
    {
        var builder = new GeoJsonFeatures.Builder();
        foreach (var member in element.EnumerateObject())
        {
            builder.Add(member.Name, member.Value);
        }

        if (builder.Finish(position, sourceSrid, out var feature, out var skip))
        {
            return RawRecord.Parsed(position, feature!);
        }

        if (!_skipMalformed)
        {
            throw new IngestFormatException(
                $"Line {position}: {GeoJsonFeatures.Describe(skip!.Reason)} ({skip.Detail}).");
        }

        return RawRecord.Dropped(position, skip!.Reason, skip.Detail);
    }

    private bool TryParse(string line, int position, out JsonDocument? document)
    {
        try
        {
            document = JsonDocument.Parse(line);
            _pendingSkip = null;
            return true;
        }
        catch (JsonException exception)
        {
            _pendingSkip = new IngestSkip(position, IngestSkipReason.RecordMalformed, exception.Message);
            _parseFailure = exception.Message;
            document = null;
            return false;
        }
    }

    private async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (!_drained)
        {
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                _drained = true;
                return null;
            }

            _lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            return line;
        }

        return null;
    }
}

/// <summary>
/// The GeoJSON feature shape shared by both readers: property scalars, an
/// optional source identity and a geometry decoded straight into a core value.
/// </summary>
internal static class GeoJsonFeatures
{
    /// <summary>Whether a record was dropped, in words a caller can act on.</summary>
    public static string Describe(IngestSkipReason reason) => reason switch
    {
        IngestSkipReason.RecordMalformed => "the record is malformed",
        IngestSkipReason.GeometryInvalid => "the geometry is invalid",
        IngestSkipReason.AttributeInvalid => "an attribute value is invalid",
        IngestSkipReason.FieldNotInferred => "a field is not in the inferred schema",
        IngestSkipReason.CrsConflict => "a declared CRS conflicts with the decode",
        _ => reason.ToString(),
    };

    /// <summary>
    /// Accumulates one feature's members and turns them into a record. The
    /// members are fed in rather than taken as a collection because
    /// <see cref="JsonElement.ObjectEnumerator"/> is a ref struct, which no
    /// method may accept or return.
    /// </summary>
    public sealed class Builder
    {
        private readonly Dictionary<string, object?> _properties = new(StringComparer.Ordinal);
        private JsonElement? _geometry;
        private JsonElement? _id;
        private IngestSkip? _skip;

        /// <summary>Records one member of a feature object.</summary>
        public void Add(string name, JsonElement value)
        {
            switch (name)
            {
                case "properties":
                    if (value.ValueKind != JsonValueKind.Object)
                    {
                        _skip = new IngestSkip(0, IngestSkipReason.RecordMalformed, "a feature's 'properties' must be an object or null");
                        return;
                    }

                    foreach (var property in value.EnumerateObject())
                    {
                        _properties[property.Name] = Scalar(property.Value);
                    }

                    break;
                case "geometry":
                    _geometry = value;
                    break;
                case "id":
                    _id = value;
                    break;
                default:
                    break;
            }
        }

        /// <summary>Builds the record, or the typed reason the feature was dropped.</summary>
        public bool Finish(int position, int sourceSrid, out RawFeature? feature, out IngestSkip? skip)
        {
            feature = null;
            if (_skip is { } failure)
            {
                skip = failure with { Record = position };
                return false;
            }

            if (TryReadGeometry(_geometry, position, sourceSrid, out var geometry, out skip) is false)
            {
                return false;
            }

            feature = new RawFeature(ReadId(_id), _properties, geometry);
            return true;
        }
    }

    private static bool TryReadGeometry(
        JsonElement? element, int position, int sourceSrid, out IGeometry? geometry, out IngestSkip? skip)
    {
        geometry = null;
        skip = null;
        if (element is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            skip = new IngestSkip(position, IngestSkipReason.GeometryInvalid, "a GeoJSON 'geometry' must be an object or null");
            return false;
        }

        try
        {
            geometry = GeoJsonGeometryCodec.Decode(value, CoordinateReference.Epsg(sourceSrid));
            return true;
        }
        catch (IngestFormatException exception)
        {
            skip = new IngestSkip(position, IngestSkipReason.GeometryInvalid, exception.Message);
            return false;
        }
    }

    private static string? ReadId(JsonElement? id)
    {
        if (id is not { } value)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt64(out var integer)
                ? integer.ToString(CultureInfo.InvariantCulture)
                : value.GetDouble().ToString("R", CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static object? Scalar(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return element.GetBoolean();
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt64(out var integer) ? (object)integer : element.GetDouble();
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        return element.GetRawText();
    }
}
