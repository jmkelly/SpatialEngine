using System.Buffers.Binary;
using System.Text;
using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.Core.Geometry;
using Spatial.Core.Geometry.Codec;

namespace Spatial.Core.Tests;

/// <summary>
/// Length/precondition guard pins for <see cref="FeatureBatchCodec"/> (T-006).
/// The canonical binary format (ADR-0020) is the wire surface, so every count
/// and length is validated before allocation: these tests pin the truncated,
/// negative-length and overflow behaviour — including exact-boundary cases —
/// so a mutation pass cannot weaken a guard unnoticed.
/// </summary>
public class FeatureBatchCodecGuardTests
{
    [Fact]
    public void Schema_count_precondition_scales_with_minimum_per_field()
    {
        // Two declared fields need at least 14 bytes; with only 8 remaining
        // the guard must fire (a weakened minimum, e.g. division instead of
        // multiplication, would proceed into the field stream).
        AssertFailed(Concat(Header(), Int32(2), new byte[8]), "schema declares 2 fields but only 8 bytes remain");
    }

    [Fact]
    public void Version_mismatch_error_pins_byte_offset_5()
    {
        AssertFailed(Concat(Header(version: 2)), "byte offset 5");
        AssertFailed(Concat(Header(version: 2)), "unsupported feature batch format version 2");
        AssertFailed(Concat(Header(version: 0)), "byte offset 5");
    }

    [Fact]
    public void Feature_count_precondition_pins_exact_truncation_message()
    {
        // One-field schema: each feature needs at least 5 bytes (id length + marker).
        var head = Concat(Header(), Int32(1), String("f"), Kind(AttributeKind.Int64), [0x00], [0x00]);

        // Only 4 bytes remain after the count: the precondition must fire with counts.
        AssertFailed(Concat(head, Int32(1), new byte[4]), "declares 1 features but only 4 bytes remain");
    }

    [Fact]
    public void Feature_count_precondition_passes_on_exact_boundary()
    {
        // Exactly 5 bytes remain for 1 feature (minimum 5): the precondition
        // passes and decoding proceeds to the attribute stream, which then
        // fails on the missing null marker — never on the count guard.
        var head = Concat(Header(), Int32(1), String("f"), Kind(AttributeKind.Int64), [0x00], [0x00]);
        var error = AssertFailed(Concat(head, Int32(1), Int32(1), [0x41]));

        Assert.Contains("expected a null marker", error);
        Assert.DoesNotContain("declares", error);
    }

    [Fact]
    public void Feature_count_precondition_scales_with_field_count()
    {
        // Two-field schema: minimum 6 bytes per feature, so 2 features need 12.
        // With only 8 bytes remaining the guard must fire (a weakened
        // minimum, e.g. subtraction instead of addition, would proceed).
        var head = Concat(
            Header(),
            Int32(2),
            String("a"), Kind(AttributeKind.Int64), [0x00], [0x00],
            String("b"), Kind(AttributeKind.Int64), [0x00], [0x00]);

        AssertFailed(Concat(head, Int32(2), new byte[8]), "declares 2 features but only 8 bytes remain");
    }

    [Fact]
    public void Feature_count_precondition_is_overflow_safe()
    {
        // int.MaxValue features would overflow a 32-bit minimum computation;
        // the (long) arithmetic must reject without allocating.
        var head = Concat(Header(), Int32(1), String("f"), Kind(AttributeKind.Int64), [0x00], [0x00]);

        var error = AssertFailed(Concat(head, Int32(int.MaxValue)));
        Assert.Contains("declares 2147483647 features but only 0 bytes remain", error);
    }

    [Fact]
    public void Geometry_negative_length_rejected()
    {
        AssertFailed(FeaturePayload(AttributeKind.Geometry, Int32(-1)), "invalid geometry payload length");
    }

    [Fact]
    public void Geometry_truncated_length_prefix_rejected()
    {
        // Fewer than 4 bytes remain for the geometry length prefix.
        AssertFailed(FeaturePayload(AttributeKind.Geometry, [0x01, 0x02]), "invalid geometry payload length");
    }

    [Fact]
    public void Geometry_exact_length_payload_decodes()
    {
        // Length exactly equal to the remaining input passes the guard and decodes.
        var geometry = GeometryFactory.CreatePoint(1.5, -2.25);
        var encoded = GeometryCodec.Encode(geometry);
        var bytes = FeaturePayload(AttributeKind.Geometry, Int32(encoded.Length), encoded);

        Assert.True(FeatureBatchCodec.TryDecode(bytes, out var batch, out var error), $"Expected decode to succeed: {error}");
        Assert.Equal(geometry, Assert.IsType<Point>(batch![0][0].GeometryValue));
    }

    [Fact]
    public void Geometry_zero_length_payload_reports_decode_error()
    {
        // Length 0 satisfies the length guard (0 < 0 is false) and proceeds
        // to geometry decoding, which rejects the empty payload — a guard
        // widened to <= would report an invalid length instead.
        AssertFailed(FeaturePayload(AttributeKind.Geometry, Int32(0)), "Invalid canonical geometry");
    }

    [Fact]
    public void DateTimeOffset_boundary_values_decode()
    {
        // Ticks at the absolute limits with a zero offset are valid.
        AssertDateTimeRoundTrips(0, 0, new DateTimeOffset(0, TimeSpan.Zero));
        AssertDateTimeRoundTrips(DateTime.MaxValue.Ticks, 0, new DateTimeOffset(DateTime.MaxValue.Ticks, TimeSpan.Zero));

        // Local representations exactly at the limits are valid.
        const long minutes840 = 840 * TimeSpan.TicksPerMinute;
        AssertDateTimeRoundTrips(minutes840, -840, new DateTimeOffset(0, TimeSpan.FromMinutes(-840)));
        AssertDateTimeRoundTrips(
            DateTime.MaxValue.Ticks - minutes840, 840,
            new DateTimeOffset(DateTime.MaxValue.Ticks, TimeSpan.FromMinutes(840)));
    }

    [Fact]
    public void DateTimeOffset_out_of_range_utc_ticks_rejected_even_with_valid_local_time()
    {
        // utcTicks = -1 with offset +840 (and MaxValue.Ticks + 1 with -840)
        // both yield a valid local time, so only the UTC range guard
        // rejects them — a weakened guard would accept.
        AssertFailed(DateTimePayload(-1, 840), "truncated or out of range");
        AssertFailed(DateTimePayload(DateTime.MaxValue.Ticks + 1, -840), "truncated or out of range");
    }

    [Fact]
    public void DateTimeOffset_just_out_of_range_rejected()
    {
        // Offsets one minute past the ±840 whole-minute range.
        AssertFailed(DateTimePayload(0, 841), "truncated or out of range");
        AssertFailed(DateTimePayload(0, -841), "truncated or out of range");

        // Local ticks one tick past each limit.
        AssertFailed(DateTimePayload(DateTime.MaxValue.Ticks, 1), "truncated or out of range");
        AssertFailed(DateTimePayload(0, -1), "truncated or out of range");
    }

    [Fact]
    public void String_negative_length_rejected()
    {
        AssertFailed(FeaturePayload(AttributeKind.String, Int32(-1)), "string payload is truncated");
        AssertFailed(FeatureIdPayload(Int32(-1)), "id is truncated");
    }

    // ----- fixtures -----

    private static void AssertDateTimeRoundTrips(long utcTicks, short offsetMinutes, DateTimeOffset expected)
    {
        var bytes = DateTimePayload(utcTicks, offsetMinutes);

        Assert.True(FeatureBatchCodec.TryDecode(bytes, out var batch, out var error), $"Expected decode to succeed: {error}");
        Assert.Equal(expected, batch![0][0].DateTimeOffsetValue);
    }

    private static byte[] DateTimePayload(long utcTicks, int offsetMinutes) =>
        FeaturePayload(AttributeKind.DateTimeOffset, Int64(utcTicks), Int16((short)offsetMinutes));

    private static byte[] FeatureIdPayload(params byte[][] idChunks)
    {
        var schemaChunks = FieldChunks("f", AttributeKind.Int64);
        return Payload(Int32(1), schemaChunks[0], schemaChunks[1], [0x00], [0x00], Int32(1), Concat(idChunks), [0x00], Int64(1));
    }

    private static byte[] FeaturePayload(AttributeKind kind, params byte[][] payloadChunks)
    {
        var schemaChunks = FieldChunks("f", kind);
        return Payload(Int32(1), schemaChunks[0], schemaChunks[1], [0x00], [0x00], Int32(1), String("f"), [0x00], Concat(payloadChunks));
    }

    private static byte[][] FieldChunks(string name, AttributeKind kind) => [String(name), Kind(kind)];

    private static byte[] Payload(params byte[][] chunks) => Concat(Header(), Concat(chunks));

    private static byte[] Header(byte version = 1)
    {
        var bytes = new byte[FeatureBatchCodec.HeaderLength];
        FeatureBatchCodec.Magic.CopyTo(bytes);
        bytes[^1] = version;
        return bytes;
    }

    private static byte[] Concat(params byte[][] chunks) =>
        chunks.SelectMany(chunk => chunk).ToArray();

    private static byte[] Kind(AttributeKind kind) => [(byte)kind];

    private static byte[] String(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Concat(Int32(bytes.Length), bytes);
    }

    private static byte[] Int32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Int64(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Int16(short value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static string AssertFailed(byte[] bytes)
    {
        Assert.False(FeatureBatchCodec.TryDecode(bytes, out _, out var error), $"Expected decode to fail for: {Convert.ToHexString(bytes)}");
        Assert.False(string.IsNullOrWhiteSpace(error));

        var exception = Assert.Throws<FeatureBatchFormatException>(() => FeatureBatchCodec.Decode(bytes));
        Assert.Equal(error, exception.Message);
        return error;
    }

    private static string AssertFailed(byte[] bytes, string messageFragment)
    {
        var error = AssertFailed(bytes);
        Assert.Contains(messageFragment, error);
        return error;
    }
}
