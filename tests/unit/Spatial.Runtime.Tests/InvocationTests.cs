using Spatial.Core.Features;
using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Capabilities;
using Spatial.Runtime.Tests.Fixtures;

namespace Spatial.Runtime.Tests;

/// <summary>
/// Invocation routing: success, structured errors, deadlines, cancellation,
/// progress and permissions across the whole runtime (Epic D "invocation
/// routing", "errors and diagnostics", "cancellation and permissions").
/// </summary>
public sealed class InvocationTests
{
    private static readonly CapabilityId Count = ExampleFeatureProvider.CountCapability;

    [Fact]
    public async Task Count_returns_the_feature_count_with_provenance()
    {
        var host = InMemoryComponentHost.Create();
        var invocation = CapabilityInvocation.Create(Count, new Dictionary<string, object?>
        {
            ["batch"] = FixtureBatches.Points((0, 0), (1, 1), (2, 2)),
        });

        var outcome = await host.Runtime.InvokeAsync(invocation);

        Assert.True(outcome.IsSuccess);
        Assert.True(outcome.TryGetValue(out var value));
        var count = Assert.IsType<FeatureBatch>(value);
        Assert.Equal(3, count.Features[0].Attributes[0].Int64Value);

        Assert.Equal(ExampleFeatureProvider.ProviderIdentifier, outcome.Provenance.Provider);
        Assert.Equal(ResolutionStep.FirstHealthy, outcome.Provenance.Step);
        Assert.Equal(Count, outcome.Provenance.Capability);
        Assert.True(outcome.Provenance.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Missing_or_typed_arguments_fail_with_invalid_arguments()
    {
        var host = InMemoryComponentHost.Create();

        var outcome = await host.Runtime.InvokeAsync(
            CapabilityInvocation.Create(Count, new Dictionary<string, object?>()));

        Assert.False(outcome.IsSuccess);
        var error = Assert.IsType<CapabilityError>(outcome.Error);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, error.Kind);
        Assert.Equal("invalid.arguments", error.Code);
        Assert.Contains("'batch'", error.Message);
    }

    [Fact]
    public async Task Envelope_denies_callers_without_the_required_permission()
    {
        var host = InMemoryComponentHost.Create();
        var invocation = CapabilityInvocation.Create(
            ExampleFeatureProvider.EnvelopeCapability,
            new Dictionary<string, object?> { ["batch"] = FixtureBatches.Points((0, 0)) });

        var outcome = await host.Runtime.InvokeAsync(invocation);

        Assert.False(outcome.IsSuccess);
        var error = Assert.IsType<CapabilityError>(outcome.Error);
        Assert.Equal(CapabilityErrorKind.PermissionDenied, error.Kind);
        Assert.Equal("permission.denied", error.Code);
        Assert.Contains(ExampleFeatureProvider.ReadPermission.Name, error.Message);
    }

    [Fact]
    public async Task Envelope_succeeds_for_callers_with_the_required_permission()
    {
        var host = InMemoryComponentHost.Create();
        var invocation = CapabilityInvocation.Create(
            ExampleFeatureProvider.EnvelopeCapability,
            new Dictionary<string, object?> { ["batch"] = FixtureBatches.Points((0, 0), (2, 3), (1, 1)) })
            with
        { GrantedPermissions = new HashSet<Permission> { ExampleFeatureProvider.ReadPermission } };

        var outcome = await host.Runtime.InvokeAsync(invocation);

        Assert.True(outcome.IsSuccess);
        Assert.True(outcome.TryGetValue(out var value));
        var batch = Assert.IsType<FeatureBatch>(value);
        var attributes = batch.Features[0].Attributes;
        Assert.Equal(0, attributes[0].DoubleValue);
        Assert.Equal(0, attributes[1].DoubleValue);
        Assert.Equal(2, attributes[2].DoubleValue);
        Assert.Equal(3, attributes[3].DoubleValue);
    }

    [Fact]
    public async Task Envelope_rejects_a_batch_without_geometry()
    {
        var host = InMemoryComponentHost.Create();
        var schema = new FeatureSchema([new FieldDefinition("name", AttributeKind.String)]);
        var batch = new FeatureBatch(
            schema,
            [new Feature(new FeatureId("x"), schema, [AttributeValue.FromString("solo")])]);
        var invocation = CapabilityInvocation.Create(
            ExampleFeatureProvider.EnvelopeCapability,
            new Dictionary<string, object?> { ["batch"] = batch })
            with
        { GrantedPermissions = new HashSet<Permission> { ExampleFeatureProvider.ReadPermission } };

        var outcome = await host.Runtime.InvokeAsync(invocation);

        Assert.Equal(CapabilityErrorKind.InvalidArguments, outcome.Error?.Kind);
    }

    [Fact]
    public async Task Cancelling_an_invocation_yields_a_cancelled_failure()
    {
        var host = InMemoryComponentHost.Create();
        using var cts = new CancellationTokenSource();
        var invocation = CapabilityInvocation.Create(
            ExampleFeatureProvider.SleepCapability,
            new Dictionary<string, object?> { ["milliseconds"] = 600L })
            with
        { CancellationToken = cts.Token };
        var task = host.Runtime.InvokeAsync(invocation);

        await Task.Delay(30);
        await cts.CancelAsync();

        var outcome = await task;
        Assert.Equal(CapabilityErrorKind.Cancelled, outcome.Error?.Kind);
        Assert.Equal("operation.cancelled", outcome.Error?.Code);
    }

    [Fact]
    public async Task Exceeding_the_deadline_yields_a_deadline_exceeded_failure()
    {
        var host = InMemoryComponentHost.Create();
        var invocation = CapabilityInvocation.Create(
            ExampleFeatureProvider.SleepCapability,
            new Dictionary<string, object?> { ["milliseconds"] = 1500L })
            with
        { Deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(30) };

        var outcome = await host.Runtime.InvokeAsync(invocation);

        Assert.Equal(CapabilityErrorKind.DeadlineExceeded, outcome.Error?.Kind);
        Assert.Equal("deadline.exceeded", outcome.Error?.Code);
        Assert.NotNull(outcome.Provenance.Provider);
        Assert.NotNull(outcome.Provenance.Deadline);
    }

    [Fact]
    public async Task An_already_passed_deadline_fails_without_touching_the_provider()
    {
        var host = InMemoryComponentHost.Create();
        var invocation = CapabilityInvocation.Create(Count, new Dictionary<string, object?>()
        {
            ["batch"] = FixtureBatches.Points((0, 0)),
        })
            with
        { Deadline = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1) };

        var outcome = await host.Runtime.InvokeAsync(invocation);

        Assert.Equal(CapabilityErrorKind.DeadlineExceeded, outcome.Error?.Kind);
        Assert.Null(outcome.Provenance.Provider);
    }

    [Fact]
    public async Task A_provider_exception_becomes_a_structured_provider_failure()
    {
        var (_, runtime) = CreateRuntime(
            new StubProvider(
                "alpha", 1, [Count],
                _ => throw new InvalidOperationException("boom")));

        var outcome = await runtime.InvokeAsync(
            CapabilityInvocation.Create(Count, new Dictionary<string, object?>()));

        Assert.Equal(CapabilityErrorKind.ProviderFailure, outcome.Error?.Kind);
        Assert.Equal("provider.failure", outcome.Error?.Code);
        Assert.Contains("alpha@1", outcome.Error?.Message);
        Assert.Contains("InvalidOperationException", outcome.Error?.Message);
        Assert.Contains("boom", outcome.Error?.Message);
    }

    [Fact]
    public async Task A_null_provider_result_becomes_a_contract_violation()
    {
        var (_, runtime) = CreateRuntime(
            new StubProvider("alpha", 1, [Count], _ => default));

        var outcome = await runtime.InvokeAsync(
            CapabilityInvocation.Create(Count, new Dictionary<string, object?>()));

        Assert.Equal(CapabilityErrorKind.ContractViolation, outcome.Error?.Kind);
        Assert.Contains("no result", outcome.Error?.Message);
    }

    [Fact]
    public async Task A_failure_without_an_error_becomes_a_contract_violation()
    {
        var (_, runtime) = CreateRuntime(
            new StubProvider(
                "alpha", 1, [Count],
                _ => new ValueTask<CapabilityResult>(new CapabilityFailure(null!))));

        var outcome = await runtime.InvokeAsync(
            CapabilityInvocation.Create(Count, new Dictionary<string, object?>()));

        Assert.Equal(CapabilityErrorKind.ContractViolation, outcome.Error?.Kind);
        Assert.Contains("without an error", outcome.Error?.Message);
    }

    [Fact]
    public async Task An_unknown_capability_reports_actionable_diagnostics()
    {
        var host = InMemoryComponentHost.Create();
        var invocation = CapabilityInvocation.Create(
            CapabilityId.Parse("spatial.geometry.buffer@1"),
            new Dictionary<string, object?>());

        var outcome = await host.Runtime.InvokeAsync(invocation);

        Assert.Equal(CapabilityErrorKind.CapabilityNotFound, outcome.Error?.Kind);
        Assert.Contains("No provider serves spatial.geometry.buffer@1", outcome.Error?.Message);
        Assert.Contains(Count.ToString(), outcome.Error?.Message);
        Assert.Null(outcome.Provenance.Provider);
    }

    [Fact]
    public async Task An_incompatible_explicit_pin_reports_why_it_cannot_serve()
    {
        var (_, runtime) = CreateRuntime(
            new StubProvider("alpha", 1, CapabilityId.Parse("spatial.feature.write@1")),
            new StubProvider("beta", 1, Count));
        var invocation = CapabilityInvocation.Create(Count, new Dictionary<string, object?>());
        var options = new InvocationOptions(ExplicitProvider: ProviderId.Parse("alpha@1"));

        var outcome = await runtime.InvokeAsync(invocation, options);

        Assert.Equal(CapabilityErrorKind.ProviderUnavailable, outcome.Error?.Kind);
        Assert.Contains("alpha@1", outcome.Error?.Message);
        Assert.Contains("cannot serve", outcome.Error?.Message);
    }

    [Fact]
    public async Task An_unhealthy_explicit_pin_reports_the_health()
    {
        var (registry, runtime) = CreateRuntime(new StubProvider("alpha", 1, Count));
        registry.SetHealth(ProviderId.Parse("alpha@1"), ProviderHealth.Unhealthy);
        var invocation = CapabilityInvocation.Create(Count, new Dictionary<string, object?>());
        var options = new InvocationOptions(ExplicitProvider: ProviderId.Parse("alpha@1"));

        var outcome = await runtime.InvokeAsync(invocation, options);

        Assert.Equal(CapabilityErrorKind.ProviderUnavailable, outcome.Error?.Kind);
        Assert.Contains("Unhealthy", outcome.Error?.Message);
    }

    [Fact]
    public async Task Progress_reports_are_delivered_in_order_with_valid_fractions()
    {
        var host = InMemoryComponentHost.Create();
        var points = new (double X, double Y)[250];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = (i, i);
        }

        var batch = FixtureBatches.Points(points);
        var reports = new List<ProgressReport>();
        var invocation = CapabilityInvocation.Create(Count, new Dictionary<string, object?> { ["batch"] = batch })
            with
        { Progress = new Progress<ProgressReport>(reports.Add) };

        var outcome = await host.Runtime.InvokeAsync(invocation);

        Assert.True(outcome.IsSuccess);
        Assert.Equal(3, reports.Count);
        Assert.All(reports, report => Assert.InRange(report.Fraction!.Value, 0, 1));
        Assert.Equal(1, reports[^1].Fraction);
    }

    [Fact]
    public async Task Explicit_provider_option_routes_to_the_requested_provider()
    {
        var (_, runtime) = CreateRuntime(
            new StubProvider("alpha", 1, Count),
            new StubProvider("zeta", 1, Count));
        var invocation = CapabilityInvocation.Create(Count, new Dictionary<string, object?>());
        var options = new InvocationOptions(ExplicitProvider: ProviderId.Parse("zeta@1"));

        var outcome = await runtime.InvokeAsync(invocation, options);

        Assert.True(outcome.IsSuccess);
        Assert.True(outcome.TryGetValue(out var value));
        Assert.Equal(ProviderId.Parse("zeta@1"), value);
        Assert.Equal(ProviderId.Parse("zeta@1"), outcome.Provenance.Provider);
        Assert.Equal(ResolutionStep.Explicit, outcome.Provenance.Step);
    }

    private static (CapabilityRegistry Registry, CapabilityRuntime Runtime) CreateRuntime(
        params ICapabilityProvider[] providers)
    {
        var registry = new CapabilityRegistry();
        foreach (var provider in providers)
        {
            registry.Register(provider);
        }

        return (registry, new CapabilityRuntime(registry));
    }
}
