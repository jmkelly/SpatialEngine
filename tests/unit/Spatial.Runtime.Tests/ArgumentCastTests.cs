using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Tests;

/// <summary>
/// The argument cast surface of <see cref="CapabilityInvocation"/>: the wire
/// codec decodes JSON integral numbers as int32 (ADR-0030 scalars cross as
/// JSON numbers) while contracts declare int64 — int/long widening makes
/// every provider's <c>TryGetArgument</c> read HTTP and worker numbers
/// regardless of which JSON number width the codec produced.
/// </summary>
public sealed class ArgumentCastTests
{
    [Fact]
    public void A_wire_int_is_read_as_long()
    {
        var invocation = CapabilityInvocation.Create(
            CapabilityId.Parse("fixture.sleep@1"),
            new Dictionary<string, object?> { ["milliseconds"] = 15_000 });

        Assert.True(invocation.TryGetArgument<long>("milliseconds", out var milliseconds));
        Assert.Equal(15_000L, milliseconds);
    }

    [Fact]
    public void A_long_is_still_read_as_long()
    {
        var invocation = CapabilityInvocation.Create(
            CapabilityId.Parse("fixture.sleep@1"),
            new Dictionary<string, object?> { ["milliseconds"] = 15_000L });

        Assert.True(invocation.TryGetArgument<long>("milliseconds", out var milliseconds));
        Assert.Equal(15_000L, milliseconds);
    }

    [Fact]
    public void An_int64_within_int32_range_is_read_as_int()
    {
        var invocation = CapabilityInvocation.Create(
            CapabilityId.Parse("fixture.echo@1"),
            new Dictionary<string, object?> { ["srid"] = 4326L });

        Assert.True(invocation.TryGetArgument<int>("srid", out var srid));
        Assert.Equal(4326, srid);
    }

    [Fact]
    public void An_int64_beyond_int32_is_not_read_as_int()
    {
        var invocation = CapabilityInvocation.Create(
            CapabilityId.Parse("fixture.echo@1"),
            new Dictionary<string, object?> { ["milliseconds"] = 3_000_000_000L });

        Assert.False(invocation.TryGetArgument<int>("milliseconds", out _));
    }

    [Fact]
    public void A_double_is_not_read_as_long()
    {
        var invocation = CapabilityInvocation.Create(
            CapabilityId.Parse("fixture.sleep@1"),
            new Dictionary<string, object?> { ["milliseconds"] = 1.5 });

        Assert.False(invocation.TryGetArgument<long>("milliseconds", out _));
        Assert.False(invocation.TryGetArgument<int>("milliseconds", out _));
    }

    [Fact]
    public void An_unsupported_type_is_not_read()
    {
        var invocation = CapabilityInvocation.Create(
            CapabilityId.Parse("fixture.echo@1"),
            new Dictionary<string, object?> { ["text"] = "hello" });

        Assert.False(invocation.TryGetArgument<long>("text", out _));
    }
}
