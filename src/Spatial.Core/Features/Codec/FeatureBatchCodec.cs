using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Core.Features.Codec;

/// <summary>
/// Canonical binary interchange for feature batches, version 1. The format is
/// deterministic: equal batches encode to identical bytes. All integers and
/// doubles are little-endian; all strings are strict UTF-8.
///
/// <code>
/// Header:
///   byte[5]   magic "SFBAT"
///   byte      format version (1)
///
/// Schema:
///   int32     field count
///   per field:
///     string  name (int32 byte length, UTF-8 bytes)
///     byte    kind (AttributeKind value; never Null)
///     byte    nullable (0 or 1)
///     byte    hasDescription (0 or 1)
///     when 1: string description
///
/// Features:
///   int32     feature count
///   per feature:
///     string  id (int32 byte length, UTF-8 bytes)
///     per field, in schema order:
///       byte  isNull (0 = value follows, 1 = null; 1 only on nullable fields)
///       when 0, payload by kind:
///         Boolean:        byte (0 or 1)
///         Int64:          int64
///         Double:         double
///         String:         string
///         Geometry:       int32 byte length, canonical geometry (GeometryCodec)
///         DateTimeOffset: int64 UTC ticks, int16 offset in whole minutes
///         Guid:           16 bytes
/// </code>
///
/// Geometry attributes embed the canonical geometry codec from
/// <see cref="GeometryCodec"/> byte-for-byte. Every count and length is
/// validated against the remaining input before allocation. Decode errors are
/// actionable: <see cref="FeatureBatchFormatException"/> (or
/// <see cref="TryDecode(ReadOnlySpan{byte}, out FeatureBatch?, out string?)"/>'s
/// error) carries the offending byte offset.
///
/// The projection overloads (<see cref="Decode(ReadOnlySpan{byte}, FeatureSchema)"/>)
/// decode a batch written under a wider schema, keeping only the fields of a
/// decodable prefix target schema — the append-only column evolution test
/// vehicle.
/// </summary>
public static class FeatureBatchCodec
{
    /// <summary>Current canonical batch format version.</summary>
    public const byte FormatVersion = 1;

    /// <summary>Fixed header size: 5 magic bytes plus the version byte.</summary>
    public const int HeaderLength = 6;

    /// <summary>The canonical magic bytes "SFBAT".</summary>
    public static ReadOnlySpan<byte> Magic => "SFBAT"u8;

    /// <summary>Encodes a batch to its canonical binary form (one allocation).</summary>
    public static byte[] Encode(FeatureBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var totalLength = ComputeBatchLength(batch);
        if (totalLength > int.MaxValue)
        {
            throw new ArgumentException($"Feature batch is too large to encode ({totalLength} bytes).", nameof(batch));
        }

        var buffer = new byte[(int)totalLength];
        var writer = new Writer(buffer);
        writer.WriteBytes(Magic);
        writer.WriteByte(FormatVersion);
        WriteSchema(ref writer, batch.Schema);
        writer.WriteInt32(batch.Count);
        foreach (var feature in batch.Features)
        {
            WriteFeature(ref writer, batch.Schema, feature);
        }

        return buffer;
    }

    /// <summary>Decodes a batch, throwing <see cref="FeatureBatchFormatException"/> on invalid input.</summary>
    public static FeatureBatch Decode(ReadOnlySpan<byte> data)
    {
        if (!TryDecode(data, out var batch, out var error))
        {
            throw new FeatureBatchFormatException(error);
        }

        return batch!;
    }

    /// <summary>
    /// Decodes a batch. On failure, <paramref name="error"/> describes the
    /// problem with the offending byte offset; no exception is thrown.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, [NotNullWhen(true)] out FeatureBatch? batch, [NotNullWhen(false)] out string? error)
    {
        if (!TryParseHeader(data, out var reader, out error))
        {
            batch = null;
            return false;
        }

        if (!TryReadSchema(ref reader, out var schema, out error))
        {
            batch = null;
            return false;
        }

        return TryDecodeFeatures(ref reader, schema, schema, out batch, out error);
    }

    /// <summary>
    /// Decodes a batch written under a possibly wider schema, projecting it to
    /// <paramref name="targetSchema"/> (which must be decodable from the batch
    /// schema: a compatible prefix). Throws
    /// <see cref="FeatureBatchFormatException"/> on invalid input.
    /// </summary>
    public static FeatureBatch Decode(ReadOnlySpan<byte> data, FeatureSchema targetSchema)
    {
        if (!TryDecode(data, targetSchema, out var batch, out var error))
        {
            throw new FeatureBatchFormatException(error);
        }

        return batch!;
    }

    /// <summary>
    /// Projecting variant of <see cref="TryDecode(ReadOnlySpan{byte}, out FeatureBatch?, out string?)"/>.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, FeatureSchema targetSchema, [NotNullWhen(true)] out FeatureBatch? batch, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(targetSchema);

        if (!TryParseHeader(data, out var reader, out error))
        {
            batch = null;
            return false;
        }

        if (!TryReadSchema(ref reader, out var sourceSchema, out error))
        {
            batch = null;
            return false;
        }

        if (!targetSchema.TryIsDecodableFrom(sourceSchema, out var reason))
        {
            batch = null;
            error = $"Cannot decode feature batch for the target schema: {reason}";
            return false;
        }

        return TryDecodeFeatures(ref reader, sourceSchema, targetSchema, out batch, out error);
    }

    private static bool TryParseHeader(ReadOnlySpan<byte> data, out Reader reader, [NotNullWhen(false)] out string? error)
    {
        reader = new Reader(data);

        if (data.Length < HeaderLength)
        {
            error = Error(0, $"header is truncated ({data.Length} of {HeaderLength} bytes).");
            return false;
        }

        if (!data.StartsWith(Magic))
        {
            error = "Invalid feature batch at byte offset 0: expected magic 'SFBAT'.";
            return false;
        }

        var version = data[HeaderLength - 1];
        if (version != FormatVersion)
        {
            error = Error(HeaderLength - 1, $"unsupported feature batch format version {version} (expected {FormatVersion}).");
            return false;
        }

        reader.Advance(HeaderLength);
        error = null;
        return true;
    }

    private static bool TryDecodeFeatures(ref Reader reader, FeatureSchema sourceSchema, FeatureSchema targetSchema, [NotNullWhen(true)] out FeatureBatch? batch, [NotNullWhen(false)] out string? error)
    {
        if (!TryReadFeatures(ref reader, sourceSchema, targetSchema, out var features, out error))
        {
            batch = null;
            return false;
        }

        if (reader.Remaining != 0)
        {
            batch = null;
            error = $"Invalid feature batch: {reader.Remaining} trailing bytes after the feature payload at byte offset {reader.Position}.";
            return false;
        }

        batch = new FeatureBatch(targetSchema, features);
        return true;
    }

    private static long ComputeBatchLength(FeatureBatch batch)
    {
        long total = HeaderLength + ComputeSchemaLength(batch.Schema) + 4;
        foreach (var feature in batch.Features)
        {
            total += 4 + Encoding.UTF8.GetByteCount(feature.Id.Value);
            for (var i = 0; i < batch.Schema.Count; i++)
            {
                total += 1; // null marker
                if (!feature.Attributes[i].IsNull)
                {
                    total += ComputePayloadLength(batch.Schema[i].Kind, feature.Attributes[i]);
                }
            }
        }

        return total;
    }

    private static long ComputeSchemaLength(FeatureSchema schema)
    {
        long total = 4;
        foreach (var field in schema.Fields)
        {
            total += 4 + Encoding.UTF8.GetByteCount(field.Name);
            total += 3; // kind, nullable, hasDescription
            if (field.Description is { } description)
            {
                total += 4 + Encoding.UTF8.GetByteCount(description);
            }
        }

        return total;
    }

    /// <summary>
    /// Encoded payload size for one non-null attribute. Geometry attributes
    /// are encoded once for sizing and again in the write pass; a growable
    /// writer is a later optimisation.
    /// </summary>
    private static long ComputePayloadLength(AttributeKind kind, AttributeValue value) => kind switch
    {
        AttributeKind.Boolean => 1,
        AttributeKind.Int64 => 8,
        AttributeKind.Double => 8,
        AttributeKind.String => 4 + Encoding.UTF8.GetByteCount(value.StringValue),
        AttributeKind.Geometry => 4 + GeometryCodec.Encode(value.GeometryValue).Length,
        AttributeKind.DateTimeOffset => 10,
        AttributeKind.Guid => 16,
        _ => throw new ArgumentException($"Unknown attribute kind '{kind}'.", nameof(kind)),
    };

    private static void WriteSchema(ref Writer writer, FeatureSchema schema)
    {
        writer.WriteInt32(schema.Count);
        foreach (var field in schema.Fields)
        {
            writer.WriteString(field.Name);
            writer.WriteByte((byte)field.Kind);
            writer.WriteByte(field.Nullable ? (byte)1 : (byte)0);
            if (field.Description is { } description)
            {
                writer.WriteByte(1);
                writer.WriteString(description);
            }
            else
            {
                writer.WriteByte(0);
            }
        }
    }

    private static void WriteFeature(ref Writer writer, FeatureSchema schema, Feature feature)
    {
        writer.WriteString(feature.Id.Value);
        for (var i = 0; i < schema.Count; i++)
        {
            var value = feature.Attributes[i];
            if (value.IsNull)
            {
                writer.WriteByte(1);
                continue;
            }

            writer.WriteByte(0);
            WritePayload(ref writer, schema[i].Kind, value);
        }
    }

    private static void WritePayload(ref Writer writer, AttributeKind kind, AttributeValue value)
    {
        switch (kind)
        {
            case AttributeKind.Boolean:
                writer.WriteByte(value.BooleanValue ? (byte)1 : (byte)0);
                break;

            case AttributeKind.Int64:
                writer.WriteInt64(value.Int64Value);
                break;

            case AttributeKind.Double:
                writer.WriteDouble(value.DoubleValue);
                break;

            case AttributeKind.String:
                writer.WriteString(value.StringValue);
                break;

            case AttributeKind.Geometry:
                var geometry = GeometryCodec.Encode(value.GeometryValue);
                writer.WriteInt32(geometry.Length);
                writer.WriteBytes(geometry);
                break;

            case AttributeKind.DateTimeOffset:
                var dateTime = value.DateTimeOffsetValue;
                writer.WriteInt64(dateTime.UtcTicks);
                writer.WriteInt16((short)dateTime.Offset.TotalMinutes);
                break;

            case AttributeKind.Guid:
                writer.WriteGuid(value.GuidValue);
                break;

            default:
                throw new ArgumentException($"Unknown attribute kind '{kind}'.", nameof(kind));
        }
    }

    private static bool TryReadSchema(ref Reader reader, [NotNullWhen(true)] out FeatureSchema? schema, [NotNullWhen(false)] out string? error)
    {
        schema = null;
        var offset = reader.Position;

        if (!reader.TryReadInt32(out var fieldCount) || fieldCount < 0)
        {
            error = Error(offset, "invalid schema field count (must be non-negative).");
            return false;
        }

        // Each field needs at least its name length, kind, nullable and hasDescription bytes.
        if ((long)fieldCount * 7 > reader.Remaining)
        {
            error = Error(offset, $"schema declares {fieldCount} fields but only {reader.Remaining} bytes remain (each field needs at least 7 bytes).");
            return false;
        }

        var fields = new FieldDefinition[fieldCount];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < fieldCount; i++)
        {
            if (!TryReadField(ref reader, i, out var field, out error))
            {
                return false;
            }

            if (!names.Add(field.Name))
            {
                error = Error(offset, $"schema declares duplicate field name '{field.Name}'.");
                return false;
            }

            fields[i] = field;
        }

        schema = new FeatureSchema(fields);
        error = null;
        return true;
    }

    private static bool TryReadField(ref Reader reader, int index, out FieldDefinition field, [NotNullWhen(false)] out string? error)
    {
        field = default;
        var offset = reader.Position;

        if (!reader.TryReadString(out var name))
        {
            error = Error(offset, $"field {index}: name is truncated or not valid UTF-8.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            error = Error(offset, $"field {index}: name must be a non-empty string.");
            return false;
        }

        if (!reader.TryReadByte(out var kindByte))
        {
            error = Error(offset, $"field {index} '{name}': expected a kind byte; input is truncated.");
            return false;
        }

        if (!Enum.IsDefined(typeof(AttributeKind), (AttributeKind)kindByte) || (AttributeKind)kindByte == AttributeKind.Null)
        {
            error = Error(offset, $"field {index} '{name}': invalid kind byte {kindByte}.");
            return false;
        }

        if (!TryReadFlag(ref reader, "nullable", index, name, out var nullable, out error))
        {
            return false;
        }

        if (!TryReadFlag(ref reader, "description presence", index, name, out var hasDescription, out error))
        {
            return false;
        }

        if (!TryReadDescription(ref reader, index, name, hasDescription, out var description, out error))
        {
            return false;
        }

        field = new FieldDefinition(name, (AttributeKind)kindByte, nullable, description);
        error = null;
        return true;
    }

    private static bool TryReadDescription(ref Reader reader, int index, string name, bool hasDescription, out string? description, [NotNullWhen(false)] out string? error)
    {
        description = null;
        if (!hasDescription)
        {
            error = null;
            return true;
        }

        var offset = reader.Position;
        if (!reader.TryReadString(out description))
        {
            error = Error(offset, $"field {index} '{name}': description is truncated or not valid UTF-8.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            error = Error(offset, $"field {index} '{name}': description must be a non-empty string.");
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadFlag(ref Reader reader, string flag, int index, string name, out bool value, [NotNullWhen(false)] out string? error)
    {
        value = false;
        var offset = reader.Position;

        if (!reader.TryReadByte(out var byteValue))
        {
            error = Error(offset, $"field {index} '{name}': expected a {flag} byte; input is truncated.");
            return false;
        }

        if (byteValue > 1)
        {
            error = Error(offset, $"field {index} '{name}': invalid {flag} byte {byteValue} (expected 0 or 1).");
            return false;
        }

        value = byteValue == 1;
        error = null;
        return true;
    }

    private static bool TryReadFeatures(ref Reader reader, FeatureSchema sourceSchema, FeatureSchema targetSchema, out Feature[] features, [NotNullWhen(false)] out string? error)
    {
        features = [];
        var offset = reader.Position;

        if (!reader.TryReadInt32(out var featureCount) || featureCount < 0)
        {
            error = Error(offset, "invalid feature count (must be non-negative).");
            return false;
        }

        // Each feature needs at least its id length plus one null marker per field.
        var minimumPerFeature = 4 + (long)sourceSchema.Count;
        if ((long)featureCount * minimumPerFeature > reader.Remaining)
        {
            error = Error(offset, $"batch declares {featureCount} features but only {reader.Remaining} bytes remain.");
            return false;
        }

        features = new Feature[featureCount];
        for (var i = 0; i < featureCount; i++)
        {
            if (!TryReadFeature(ref reader, sourceSchema, targetSchema, i, out var feature, out error))
            {
                return false;
            }

            features[i] = feature;
        }

        error = null;
        return true;
    }

    private static bool TryReadFeature(ref Reader reader, FeatureSchema sourceSchema, FeatureSchema targetSchema, int featureIndex, [NotNullWhen(true)] out Feature? feature, [NotNullWhen(false)] out string? error)
    {
        feature = null;
        var offset = reader.Position;

        if (!reader.TryReadString(out var id))
        {
            error = Error(offset, $"feature {featureIndex}: id is truncated or not valid UTF-8.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            error = Error(offset, $"feature {featureIndex}: id must be a non-empty string.");
            return false;
        }

        var values = new AttributeValue[sourceSchema.Count];
        for (var i = 0; i < sourceSchema.Count; i++)
        {
            if (!TryReadAttribute(ref reader, sourceSchema, featureIndex, i, out values[i], out error))
            {
                return false;
            }
        }

        // The target is a validated prefix of the source, so attributes beyond
        // the target's count are read for structural validation and dropped.
        feature = targetSchema.Count == sourceSchema.Count
            ? new Feature(new FeatureId(id), targetSchema, values)
            : new Feature(new FeatureId(id), targetSchema, values.AsSpan(0, targetSchema.Count).ToArray());
        error = null;
        return true;
    }

    private static bool TryReadAttribute(ref Reader reader, FeatureSchema schema, int featureIndex, int attributeIndex, out AttributeValue value, [NotNullWhen(false)] out string? error)
    {
        value = default;
        var field = schema[attributeIndex];
        var offset = reader.Position;

        if (!reader.TryReadByte(out var isNull))
        {
            error = Error(offset, $"feature {featureIndex} attribute {attributeIndex} ('{field.Name}'): expected a null marker; input is truncated.");
            return false;
        }

        if (isNull > 1)
        {
            error = Error(offset, $"feature {featureIndex} attribute {attributeIndex} ('{field.Name}'): invalid null marker {isNull} (expected 0 or 1).");
            return false;
        }

        if (isNull == 1)
        {
            if (!field.Nullable)
            {
                error = Error(offset, $"feature {featureIndex} attribute {attributeIndex} ('{field.Name}'): null marker on a non-nullable field.");
                return false;
            }

            value = AttributeValue.Null;
            error = null;
            return true;
        }

        return TryReadPayload(ref reader, field, new AttributeSite(featureIndex, attributeIndex, offset), out value, out error);
    }

    private static bool TryReadPayload(ref Reader reader, FieldDefinition field, AttributeSite site, out AttributeValue value, [NotNullWhen(false)] out string? error)
    {
        value = default;
        var prefix = $"feature {site.FeatureIndex} attribute {site.AttributeIndex} ('{field.Name}')";

        return field.Kind switch
        {
            AttributeKind.Boolean => TryReadBooleanPayload(ref reader, site, prefix, out value, out error),
            AttributeKind.Int64 => TryReadInt64Payload(ref reader, site, prefix, out value, out error),
            AttributeKind.Double => TryReadDoublePayload(ref reader, site, prefix, out value, out error),
            AttributeKind.String => TryReadStringPayload(ref reader, site, prefix, out value, out error),
            AttributeKind.Geometry => TryReadGeometryPayload(ref reader, site, prefix, out value, out error),
            AttributeKind.DateTimeOffset => TryReadDateTimeOffsetPayload(ref reader, site, prefix, out value, out error),
            AttributeKind.Guid => TryReadGuidPayload(ref reader, site, prefix, out value, out error),
            _ => FailPayload(site, prefix, field, out value, out error),
        };
    }

    private static bool TryReadBooleanPayload(ref Reader reader, AttributeSite site, string prefix, out AttributeValue value, [NotNullWhen(false)] out string? error)
    {
        value = default;
        if (!reader.TryReadByte(out var booleanByte))
        {
            error = Error(site.Offset, $"{prefix}: expected a boolean byte; input is truncated.");
            return false;
        }

        if (booleanByte > 1)
        {
            error = Error(site.Offset, $"{prefix}: invalid boolean byte {booleanByte} (expected 0 or 1).");
            return false;
        }

        value = AttributeValue.FromBoolean(booleanByte == 1);
        error = null;
        return true;
    }

    private static bool TryReadInt64Payload(ref Reader reader, AttributeSite site, string prefix, out AttributeValue value, [NotNullWhen(false)] out string? error)
    {
        value = default;
        if (!reader.TryReadInt64(out var int64))
        {
            error = Error(site.Offset, $"{prefix}: int64 payload is truncated.");
            return false;
        }

        value = AttributeValue.FromInt64(int64);
        error = null;
        return true;
    }

    private static bool TryReadDoublePayload(ref Reader reader, AttributeSite site, string prefix, out AttributeValue value, [NotNullWhen(false)] out string? error)
    {
        value = default;
        if (!reader.TryReadDouble(out var dbl))
        {
            error = Error(site.Offset, $"{prefix}: double payload is truncated.");
            return false;
        }

        value = AttributeValue.FromDouble(dbl);
        error = null;
        return true;
    }

    private static bool TryReadStringPayload(ref Reader reader, AttributeSite site, string prefix, out AttributeValue value, [NotNullWhen(false)] out string? error)
    {
        value = default;
        if (!reader.TryReadString(out var str))
        {
            error = Error(site.Offset, $"{prefix}: string payload is truncated or not valid UTF-8.");
            return false;
        }

        value = AttributeValue.FromString(str);
        error = null;
        return true;
    }

    private static bool TryReadGeometryPayload(ref Reader reader, AttributeSite site, string prefix, out AttributeValue value, [NotNullWhen(false)] out string? error)
    {
        value = default;
        var offset = reader.Position;
        if (!reader.TryReadInt32(out var length) || length < 0 || length > reader.Remaining)
        {
            error = Error(offset, $"{prefix}: invalid geometry payload length.");
            return false;
        }

        if (!GeometryCodec.TryDecode(reader.Slice(length), out var geometry, out var geometryError))
        {
            error = Error(offset, $"{prefix}: {geometryError}");
            return false;
        }

        reader.Advance(length);
        value = AttributeValue.FromGeometry(geometry);
        error = null;
        return true;
    }

    private static bool TryReadDateTimeOffsetPayload(ref Reader reader, AttributeSite site, string prefix, out AttributeValue value, [NotNullWhen(false)] out string? error)
    {
        value = default;
        if (!reader.TryReadDateTimeOffset(out var dateTime))
        {
            error = Error(site.Offset, $"{prefix}: date-time payload is truncated or out of range.");
            return false;
        }

        value = AttributeValue.FromDateTimeOffset(dateTime);
        error = null;
        return true;
    }

    private static bool TryReadGuidPayload(ref Reader reader, AttributeSite site, string prefix, out AttributeValue value, [NotNullWhen(false)] out string? error)
    {
        value = default;
        if (!reader.TryReadGuid(out var guid))
        {
            error = Error(site.Offset, $"{prefix}: guid payload is truncated.");
            return false;
        }

        value = AttributeValue.FromGuid(guid);
        error = null;
        return true;
    }

    private static bool FailPayload(AttributeSite site, string prefix, FieldDefinition field, out AttributeValue value, [NotNullWhen(false)] out string? error)
    {
        value = default;
        error = Error(site.Offset, $"{prefix}: unsupported payload kind {field.Kind}.");
        return false;
    }

    private static string Error(int offset, string detail) =>
        FormattableString.Invariant($"Invalid feature batch at byte offset {offset}: {detail}");

    /// <summary>Identifies one attribute position for actionable error messages.</summary>
    private readonly struct AttributeSite(int FeatureIndex, int AttributeIndex, int Offset)
    {
        public int FeatureIndex { get; } = FeatureIndex;

        public int AttributeIndex { get; } = AttributeIndex;

        public int Offset { get; } = Offset;
    }

    private ref struct Reader
    {
        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public Reader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public int Position => _position;

        public int Remaining => _data.Length - _position;

        public void Advance(int count) => _position += count;

        public ReadOnlySpan<byte> Slice(int length) => _data.Slice(_position, length);

        public bool TryReadByte(out byte value)
        {
            if (_position >= _data.Length)
            {
                value = 0;
                return false;
            }

            value = _data[_position++];
            return true;
        }

        public bool TryReadInt16(out short value)
        {
            if (!BinaryPrimitives.TryReadInt16LittleEndian(_data[_position..], out value))
            {
                return false;
            }

            _position += 2;
            return true;
        }

        public bool TryReadInt32(out int value)
        {
            if (!BinaryPrimitives.TryReadInt32LittleEndian(_data[_position..], out value))
            {
                return false;
            }

            _position += 4;
            return true;
        }

        public bool TryReadInt64(out long value)
        {
            if (!BinaryPrimitives.TryReadInt64LittleEndian(_data[_position..], out value))
            {
                return false;
            }

            _position += 8;
            return true;
        }

        public bool TryReadDouble(out double value)
        {
            if (!BinaryPrimitives.TryReadDoubleLittleEndian(_data[_position..], out value))
            {
                return false;
            }

            _position += 8;
            return true;
        }

        public bool TryReadGuid(out Guid value)
        {
            if (_data.Length - _position < 16)
            {
                value = default;
                return false;
            }

            Span<byte> bytes = stackalloc byte[16];
            _data.Slice(_position, 16).CopyTo(bytes);
            _position += 16;
            value = new Guid(bytes);
            return true;
        }

        public bool TryReadDateTimeOffset(out DateTimeOffset value)
        {
            var offset = _position;
            if (!TryReadInt64(out var utcTicks) || !TryReadInt16(out var offsetMinutes) || offsetMinutes is < -840 or > 840)
            {
                value = default;
                _position = offset;
                return false;
            }

            if (utcTicks < 0 || utcTicks > DateTime.MaxValue.Ticks)
            {
                value = default;
                _position = offset;
                return false;
            }

            var localTicks = utcTicks + (offsetMinutes * TimeSpan.TicksPerMinute);
            if (localTicks < 0 || localTicks > DateTime.MaxValue.Ticks)
            {
                value = default;
                _position = offset;
                return false;
            }

            value = AttributeValue.FromUtcTicks(utcTicks, offsetMinutes);
            return true;
        }

        public bool TryReadString(out string value)
        {
            value = string.Empty;
            if (!TryReadInt32(out var length) || length < 0 || length > Remaining)
            {
                return false;
            }

            try
            {
                value = StrictUtf8.GetString(_data.Slice(_position, length));
            }
            catch (DecoderFallbackException)
            {
                return false;
            }

            _position += length;
            return true;
        }
    }

    private ref struct Writer
    {
        private readonly Span<byte> _buffer;
        private int _position;

        public Writer(Span<byte> buffer)
        {
            _buffer = buffer;
            _position = 0;
        }

        public void WriteByte(byte value) => _buffer[_position++] = value;

        public void WriteInt16(short value)
        {
            BinaryPrimitives.WriteInt16LittleEndian(_buffer[_position..], value);
            _position += 2;
        }

        public void WriteInt32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer[_position..], value);
            _position += 4;
        }

        public void WriteInt64(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_buffer[_position..], value);
            _position += 8;
        }

        public void WriteDouble(double value)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(_buffer[_position..], value);
            _position += 8;
        }

        public void WriteGuid(Guid value)
        {
            value.TryWriteBytes(_buffer[_position..]);
            _position += 16;
        }

        public void WriteString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteInt32(bytes.Length);
            bytes.CopyTo(_buffer[_position..]);
            _position += bytes.Length;
        }

        public void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(_buffer[_position..]);
            _position += bytes.Length;
        }
    }
}
