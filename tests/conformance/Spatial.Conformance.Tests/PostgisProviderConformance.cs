using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.Provider.PostGIS.Configuration;
using Spatial.Runtime.Capabilities;

namespace Spatial.Conformance.Tests;

/// <summary>
/// The shared conformance fixtures of the Phase 8 data-provider contracts
/// (plan §18, ADR-0028): every provider of the catalogue/dataset/feature/
/// transaction contracts runs the same <b>store-free</b> matrix — argument
/// validation (missing/malformed dataset ids, bbox and filter errors, batch
/// and transaction argument failures), the unconfigured-provider
/// <c>provider.unavailable</c> shape (actionable, redacted, naming the
/// launch environment), pre-cancelled <c>operation.cancelled</c> failures and
/// provenance naming the serving provider. The store-backed matrix (real
/// catalogue/schema/scan/query/write/transaction behaviour) lives in the
/// containerised integration suite.
/// </summary>
public static class PostgisProviderConformance
{
    /// <summary>Invokes one capability and awaits its runtime outcome.</summary>
    public delegate Task<CapabilityOutcome> Invoker(
        CapabilityId capability,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);

    /// <summary>A canonical feature batch (FeatureBatchCodec v1 bytes) for a low-store shape.</summary>
    public static byte[] SampleBatchBytes()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String, false),
            new FieldDefinition("geom", AttributeKind.Geometry, false),
        ]);
        return FeatureBatchCodec.Encode(new FeatureBatch(schema, []));
    }

    public static async Task RunDataProviderMatrixAsync(Invoker invoke, ProviderId expectedProvider, bool skipWireIncompatible = false)
    {
        await AssertArgumentErrorsAsync(invoke, expectedProvider, skipWireIncompatible);
        await AssertUnavailableAsync(invoke, expectedProvider);
        await AssertCancellationAsync(invoke, expectedProvider);
    }

    /// <summary>Every contract's invalid-argument shapes (no store access).</summary>
    private static async Task AssertArgumentErrorsAsync(Invoker invoke, ProviderId expectedProvider, bool skipWireIncompatible)
    {
        var batch = SampleBatchBytes();
        var cases = new (CapabilityId Capability, IReadOnlyDictionary<string, object?> Arguments, string ShouldContain)[]
        {
            (CatalogueListContract.Id, Arguments(ProviderArguments.Pattern, 42), "'pattern' must be a string"),
            (DatasetDescribeContract.Id, Arguments(), "'dataset'"),
            (DatasetDescribeContract.Id, Arguments(ProviderArguments.Dataset, "places; drop table x"), "'places; drop table x'"),
            (DatasetCreateContract.Id, Arguments(), "'dataset'"),
            (DatasetCreateContract.Id, Arguments(ProviderArguments.Dataset, "public.result"), "'batch'"),
            (DatasetCreateContract.Id, Arguments(ProviderArguments.Dataset, "public.result", ProviderArguments.Batch, new byte[] { 42, 42 }), "canonical feature batch"),
            (DatasetCreateContract.Id, Arguments(ProviderArguments.Dataset, "public.result", ProviderArguments.Batch, batch, ProviderArguments.Srid, -1), "'srid'"),
            (FeatureScanContract.Id, Arguments(), "'dataset'"),
            (FeatureQueryContract.Id, Arguments(), "'dataset'"),
            (FeatureQueryContract.Id, Arguments(ProviderArguments.Dataset, "public.places", ProviderArguments.MinX, 1.0), "all four bounds"),
            (FeatureQueryContract.Id, Arguments(ProviderArguments.Dataset, "public.places", ProviderArguments.Filter, "population >"), "filter is not valid"),
            (FeatureQueryContract.Id, Arguments(ProviderArguments.Dataset, "public.places", ProviderArguments.MinX, double.NaN, ProviderArguments.MinY, 0.0, ProviderArguments.MaxX, 1.0, ProviderArguments.MaxY, 1.0), "invalid"),
            (FeatureWriteContract.Id, Arguments(), "'dataset'"),
            (FeatureWriteContract.Id, Arguments(ProviderArguments.Dataset, "public.places"), "'batch'"),
            (FeatureWriteContract.Id, Arguments(ProviderArguments.Dataset, "public.places", ProviderArguments.Batch, batch, ProviderArguments.Transaction, "not-a-handle"), "'transaction'"),
            (TransactionCommitContract.Id, Arguments(), "'transaction'"),
            (TransactionRollbackContract.Id, Arguments(), "'transaction'"),
        };

        foreach (var (capability, arguments, shouldContain) in cases)
        {
            if (skipWireIncompatible && CarriesNonFiniteNumber(arguments))
            {
                continue;
            }

            var outcome = await invoke(capability, arguments);
            AssertProvenance(outcome, capability, expectedProvider);
            Assert.True(
                outcome.Error!.Kind == CapabilityErrorKind.InvalidArguments,
                $"{capability} with [{string.Join(",", arguments.Keys)}] must be invalid.arguments, got {outcome.Error.Kind}: {outcome.Error.Message}");
            Assert.Equal("invalid.arguments", outcome.Error.Code);
            Assert.Contains(shouldContain, outcome.Error.Message);
        }
    }

    /// <summary>A valid invocation of every contract on an unconfigured provider is a redacted, actionable unavailable.</summary>
    private static async Task AssertUnavailableAsync(Invoker invoke, ProviderId expectedProvider)
    {
        var batch = SampleBatchBytes();
        var valid = new (CapabilityId Capability, IReadOnlyDictionary<string, object?> Arguments)[]
        {
            (CatalogueListContract.Id, Arguments()),
            (DatasetDescribeContract.Id, Arguments(ProviderArguments.Dataset, "public.places")),
            (DatasetCreateContract.Id, Arguments(ProviderArguments.Dataset, "public.result", ProviderArguments.Batch, batch)),
            (FeatureScanContract.Id, Arguments(ProviderArguments.Dataset, "public.places")),
            (FeatureQueryContract.Id, Arguments(ProviderArguments.Dataset, "public.places", ProviderArguments.MinX, 1.0, ProviderArguments.MinY, 1.0, ProviderArguments.MaxX, 2.0, ProviderArguments.MaxY, 2.0)),
            (FeatureWriteContract.Id, Arguments(ProviderArguments.Dataset, "public.places", ProviderArguments.Batch, batch)),
            (TransactionBeginContract.Id, Arguments()),
        };

        foreach (var (capability, arguments) in valid)
        {
            var outcome = await invoke(capability, arguments);
            AssertProvenance(outcome, capability, expectedProvider);
            Assert.True(
                outcome.Error!.Kind == CapabilityErrorKind.ProviderUnavailable,
                $"{capability} on an unconfigured provider must be unavailable, got {outcome.Error.Kind}: {outcome.Error.Message}");
            Assert.Equal("provider.unavailable", outcome.Error.Code);
            Assert.Contains(PostgisConnectionConfiguration.EnvironmentVariable, outcome.Error.Message);
            Assert.DoesNotContain("Password", outcome.Error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>A pre-cancelled valid invocation fails with operation.cancelled before touching the store.</summary>
    private static async Task AssertCancellationAsync(Invoker invoke, ProviderId expectedProvider)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var outcome = await invoke(
            FeatureScanContract.Id,
            Arguments(ProviderArguments.Dataset, "public.places"),
            cancellation.Token);

        Assert.True(outcome.Result is CapabilityFailure { Error.Kind: CapabilityErrorKind.Cancelled });
        Assert.Equal("operation.cancelled", outcome.Error!.Code);
        AssertProvenance(outcome, FeatureScanContract.Id, expectedProvider);
    }

    /// <summary>Non-finite doubles cannot cross the JSON wire (the Phase 6 rule), so those examples are in-process only.</summary>
    private static bool CarriesNonFiniteNumber(IReadOnlyDictionary<string, object?> arguments) =>
        arguments.Values.OfType<double>().Any(value => !double.IsFinite(value));

    private static Dictionary<string, object?> Arguments(params object?[] pairs)
    {
        var arguments = new Dictionary<string, object?>(pairs.Length / 2);
        for (var i = 0; i < pairs.Length; i += 2)
        {
            arguments[(string)pairs[i]!] = pairs[i + 1];
        }

        return arguments;
    }

    private static void AssertProvenance(CapabilityOutcome outcome, CapabilityId capability, ProviderId expectedProvider)
    {
        Assert.Equal(capability, outcome.Provenance.Capability);
        Assert.Equal(expectedProvider, outcome.Provenance.Provider);
        Assert.NotNull(outcome.Provenance.Step);
        Assert.True(outcome.Provenance.Duration >= TimeSpan.Zero);
        Assert.Null(outcome.Provenance.JobId);
    }
}
