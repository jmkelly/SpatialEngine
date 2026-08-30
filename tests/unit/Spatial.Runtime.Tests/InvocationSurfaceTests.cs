using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Capabilities;

namespace Spatial.Runtime.Tests;

/// <summary>
/// The contract surface the SDK ships: invocation construction, result
/// types, progress validation and the structured error factories.
/// </summary>
public sealed class InvocationSurfaceTests
{
    private static readonly CapabilityId Count = CapabilityId.Parse("spatial.feature.count@1");

    [Fact]
    public void Create_builds_a_minimal_invocation()
    {
        var arguments = new Dictionary<string, object?> { ["batch"] = "x" };

        var invocation = CapabilityInvocation.Create(Count, arguments);

        Assert.Equal(Count, invocation.Capability);
        Assert.Equal(arguments, invocation.Arguments);
        Assert.Empty(invocation.GrantedPermissions);
        Assert.Null(invocation.Deadline);
        Assert.Null(invocation.Progress);
        Assert.Equal(CancellationToken.None, invocation.CancellationToken);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData("text")]
    [InlineData(3.5)]
    public void TryGetArgument_reads_exact_typed_arguments(object value)
    {
        var invocation = CapabilityInvocation.Create(Count, new Dictionary<string, object?> { ["arg"] = value });

        Assert.True(invocation.TryGetArgument<object>("arg", out _));
        Assert.Equal(value, invocation.Arguments["arg"]);
    }

    [Fact]
    public void TryGetArgument_is_false_for_missing_or_wrong_typed_arguments()
    {
        var invocation = CapabilityInvocation.Create(Count, new Dictionary<string, object?> { ["arg"] = "text" });

        Assert.False(invocation.TryGetArgument<string>("missing", out _));
        Assert.False(invocation.TryGetArgument<long>("arg", out _));
    }

    [Fact]
    public void Results_distinguish_success_and_failure()
    {
        var success = CapabilityResult.Success(42);
        var failure = CapabilityResult.Failure(CapabilityError.InvalidArguments("bad"));

        Assert.True(success.IsSuccess);
        Assert.Equal(42, Assert.IsType<CapabilitySuccess>(success).Value);
        Assert.False(failure.IsSuccess);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, Assert.IsType<CapabilityFailure>(failure).Error.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(1)]
    public void ProgressReport_accepts_fractions_in_range(double fraction)
    {
        var report = ProgressReport.Create(fraction, "halfway");

        Assert.Equal(fraction, report.Fraction);
        Assert.Equal("halfway", report.Message);
        Assert.True(report.Timestamp != default);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.01)]
    public void ProgressReport_rejects_fractions_out_of_range(double fraction) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ProgressReport.Create(fraction, "bad"));

    [Fact]
    public void ProgressReport_supports_uncounted_milestones()
    {
        var report = ProgressReport.Milestone("loading");

        Assert.Null(report.Fraction);
        Assert.Equal("loading", report.Message);
    }

    [Fact]
    public void Error_factories_keep_stable_codes_and_kinds()
    {
        Assert.Equal(CapabilityErrorKind.InvalidArguments, CapabilityError.InvalidArguments("m").Kind);
        Assert.Equal(CapabilityErrorKind.PermissionDenied, CapabilityError.PermissionDenied([new Permission("read")]).Kind);
        Assert.Equal(CapabilityErrorKind.DeadlineExceeded, CapabilityError.DeadlineExceeded(Count).Kind);
        Assert.Equal(CapabilityErrorKind.Cancelled, CapabilityError.Cancelled(Count).Kind);
        Assert.Equal(CapabilityErrorKind.CapabilityNotFound, CapabilityError.CapabilityNotFound("nope").Kind);
        Assert.Equal(CapabilityErrorKind.ProviderUnavailable, CapabilityError.ProviderUnavailable("down").Kind);
        Assert.Equal(CapabilityErrorKind.ContractViolation, CapabilityError.ContractViolation("bad").Kind);
        Assert.Equal(CapabilityErrorKind.ProviderFailure, CapabilityError.ProviderFailure("boom").Kind);
    }

    [Fact]
    public void PermissionDenied_lists_the_missing_permissions()
    {
        var error = CapabilityError.PermissionDenied(
            [new Permission("spatial.feature.read"), new Permission("spatial.feature.write")]);

        Assert.Equal("permission.denied", error.Code);
        Assert.Contains("spatial.feature.read", error.Message);
        Assert.Contains("spatial.feature.write", error.Message);
    }

    [Fact]
    public void Outcome_helpers_expose_value_and_error()
    {
        var successOutcome = new CapabilityOutcome(
            CapabilityResult.Success("ok"),
            new InvocationProvenance(Count, DateTimeOffset.UtcNow, TimeSpan.Zero, null, null, null));
        Assert.True(successOutcome.IsSuccess);
        Assert.True(successOutcome.TryGetValue(out var value));
        Assert.Equal("ok", value);
        Assert.Null(successOutcome.Error);

        var failureOutcome = new CapabilityOutcome(
            CapabilityResult.Failure(CapabilityError.Cancelled(Count)),
            new InvocationProvenance(Count, DateTimeOffset.UtcNow, TimeSpan.Zero, null, null, null));
        Assert.False(failureOutcome.IsSuccess);
        Assert.False(failureOutcome.TryGetValue(out _));
        Assert.Equal(CapabilityErrorKind.Cancelled, failureOutcome.Error?.Kind);
    }
}
