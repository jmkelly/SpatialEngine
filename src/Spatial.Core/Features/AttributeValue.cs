using Spatial.Core.Geometry;

namespace Spatial.Core.Features;

/// <summary>
/// A tagged-union attribute value. Value types are stored in dedicated
/// fields, so no attribute allocates a heap object: boolean, integer, double,
/// GUID and date-time values are stored inline; strings and geometries are
/// already reference types. The default value is <see cref="Null"/>.
///
/// Equality is structural and exact, following the geometry model: NaN
/// doubles compare equal, strings compare ordinally, geometries compare via
/// <see cref="GeometryComparer"/>, and date-time values compare by UTC ticks
/// <em>and</em> offset (two values representing the same instant with
/// different offsets are not equal, unlike BCL
/// <see cref="DateTimeOffset.Equals(DateTimeOffset)"/> semantics — so equal
/// values always encode to identical bytes).
/// </summary>
public readonly struct AttributeValue : IEquatable<AttributeValue>
{
    private readonly AttributeKind _kind;
    private readonly bool _boolean;
    private readonly long _int64;
    private readonly double _double;
    private readonly Guid _guid;
    private readonly short _offsetMinutes;
    private readonly object? _reference;

    private AttributeValue(AttributeKind kind) => _kind = kind;

    private AttributeValue(bool value)
    {
        _kind = AttributeKind.Boolean;
        _boolean = value;
    }

    private AttributeValue(long value)
    {
        _kind = AttributeKind.Int64;
        _int64 = value;
    }

    private AttributeValue(double value)
    {
        _kind = AttributeKind.Double;
        _double = value;
    }

    private AttributeValue(string value)
    {
        _kind = AttributeKind.String;
        _reference = value;
    }

    private AttributeValue(IGeometry value)
    {
        _kind = AttributeKind.Geometry;
        _reference = value;
    }

    private AttributeValue(DateTimeOffset value)
    {
        _kind = AttributeKind.DateTimeOffset;
        _int64 = value.UtcTicks;
        _offsetMinutes = (short)value.Offset.TotalMinutes;
    }

    private AttributeValue(Guid value)
    {
        _kind = AttributeKind.Guid;
        _guid = value;
    }

    /// <summary>The absence of a value (only valid in nullable fields).</summary>
    public static AttributeValue Null { get; } = new(AttributeKind.Null);

    public static AttributeValue FromBoolean(bool value) => new(value);

    public static AttributeValue FromInt64(long value) => new(value);

    public static AttributeValue FromDouble(double value) => new(value);

    public static AttributeValue FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new AttributeValue(value);
    }

    public static AttributeValue FromGeometry(IGeometry value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new AttributeValue(value);
    }

    public static AttributeValue FromDateTimeOffset(DateTimeOffset value) => new(value);

    public static AttributeValue FromGuid(Guid value) => new(value);

    /// <summary>The kind of the value; <see cref="AttributeKind.Null"/> when absent.</summary>
    public AttributeKind Kind => _kind;

    /// <summary>Whether the value is the absence of a value.</summary>
    public bool IsNull => _kind == AttributeKind.Null;

    public bool BooleanValue => GetChecked(AttributeKind.Boolean) ? _boolean : default;

    public long Int64Value => GetChecked(AttributeKind.Int64) ? _int64 : default;

    public double DoubleValue => GetChecked(AttributeKind.Double) ? _double : default;

    public string StringValue => GetChecked(AttributeKind.String) ? (string)_reference! : string.Empty;

    public IGeometry GeometryValue => GetChecked(AttributeKind.Geometry) ? (IGeometry)_reference! : null!;

    public DateTimeOffset DateTimeOffsetValue =>
        GetChecked(AttributeKind.DateTimeOffset) ? FromUtcTicks(_int64, _offsetMinutes) : default;

    public Guid GuidValue => GetChecked(AttributeKind.Guid) ? _guid : default;

    public bool Equals(AttributeValue other)
    {
        if (_kind != other._kind)
        {
            return false;
        }

        return EqualsPayload(other);
    }

    /// <summary>Same-kind payload comparison; only called with matching kinds.</summary>
    private bool EqualsPayload(AttributeValue other) => _kind switch
    {
        AttributeKind.Null => true,
        AttributeKind.Boolean => _boolean == other._boolean,
        AttributeKind.Int64 => _int64 == other._int64,
        AttributeKind.Double => _double.Equals(other._double),
        AttributeKind.String => EqualsString(other),
        AttributeKind.Geometry => EqualsGeometry(other),
        AttributeKind.DateTimeOffset => EqualsDateTimeOffset(other),
        AttributeKind.Guid => _guid == other._guid,
        _ => false,
    };

    /// <summary>Comparison of two date-time attributes (UTC ticks and offset).</summary>
    private bool EqualsDateTimeOffset(AttributeValue other) =>
        _int64 == other._int64 && _offsetMinutes == other._offsetMinutes;

    /// <summary>Ordinal comparison of two string attributes.</summary>
    private bool EqualsString(AttributeValue other) =>
        _reference is string left && StringComparer.Ordinal.Equals(left, other._reference as string);

    /// <summary>Structural geometry comparison of two geometry attributes.</summary>
    private bool EqualsGeometry(AttributeValue other) =>
        _reference is IGeometry left && GeometryComparer.Equals(left, other._reference as IGeometry);

    public override bool Equals(object? obj) => obj is AttributeValue other && Equals(other);

    public override int GetHashCode() => _kind switch
    {
        AttributeKind.Null => 0,
        AttributeKind.Boolean => HashCode.Combine(_kind, _boolean),
        AttributeKind.Int64 => HashCode.Combine(_kind, _int64),
        AttributeKind.Double => HashCode.Combine(_kind, _double),
        AttributeKind.String => HashCode.Combine(_kind, StringComparer.Ordinal.GetHashCode((string)_reference!)),
        AttributeKind.Geometry => HashCode.Combine(_kind, GeometryComparer.GetHashCode((IGeometry)_reference!)),
        AttributeKind.DateTimeOffset => HashCode.Combine(_kind, _int64, _offsetMinutes),
        AttributeKind.Guid => HashCode.Combine(_kind, _guid),
        _ => HashCode.Combine(_kind),
    };

    public static bool operator ==(AttributeValue left, AttributeValue right) => left.Equals(right);

    public static bool operator !=(AttributeValue left, AttributeValue right) => !left.Equals(right);

    public override string ToString() => _kind switch
    {
        AttributeKind.Null => "Null",
        AttributeKind.Boolean => $"Boolean({_boolean})",
        AttributeKind.Int64 => $"Int64({_int64})",
        AttributeKind.Double => FormattableString.Invariant($"Double({_double})"),
        AttributeKind.String => $"String({_reference})",
        AttributeKind.Geometry => $"Geometry({_reference})",
        AttributeKind.DateTimeOffset => FormattableString.Invariant($"DateTimeOffset({DateTimeOffsetValue:O})"),
        AttributeKind.Guid => $"Guid({_guid})",
        _ => FormattableString.Invariant($"Unknown({(int)_kind})"),
    };

    /// <summary>Reconstructs a <see cref="DateTimeOffset"/> from stored UTC ticks and whole-minute offset.</summary>
    internal static DateTimeOffset FromUtcTicks(long utcTicks, short offsetMinutes)
    {
        var localTicks = utcTicks + (offsetMinutes * TimeSpan.TicksPerMinute);
        return new DateTimeOffset(new DateTime(localTicks, DateTimeKind.Unspecified), TimeSpan.FromMinutes(offsetMinutes));
    }

    /// <summary>Returns true when the stored kind matches; otherwise throws an actionable error.</summary>
    private bool GetChecked(AttributeKind expected)
    {
        if (_kind == expected)
        {
            return true;
        }

        throw new InvalidOperationException(
            FormattableString.Invariant($"Attribute value is {_kind}, not {expected}. Check Kind before reading the value."));
    }
}
