using Spatial.PluginHost.DotNet.Manifest;
using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginHost.DotNet.Tests;

/// <summary>
/// Phase 5 manifest schema (plan §16 "finalise manifest schema",
/// <c>architecture/plugin-manifest.md</c>): the schema version, provider
/// identity, runtime hint and payload rules, the capability contract rules
/// that mirror the registry's, and the manifest↔provider compatibility check
/// the supervisor runs before activation.
/// </summary>
public sealed class ManifestTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-manifest-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Valid_v1_manifest_loads_and_validates()
    {
        WriteManifest(FixtureManifest.V1());

        var manifest = PluginManifestLoader.Load(_directory);

        Assert.Equal(PluginManifest.SchemaVersionV1, manifest.SchemaVersion);
        Assert.Equal("fixture@1", manifest.Id);
        Assert.Equal("fixture@1", manifest.ProviderId);
        Assert.Equal("dotnet", manifest.Runtime);
        Assert.Equal(FixtureManifest.AssemblyName, manifest.Assembly);
        Assert.Equal(FixtureManifest.V1ProviderType, manifest.AssemblyType);
        Assert.Equal(6, manifest.Capabilities.Count);
        Assert.Empty(PluginManifestValidator.DescribeProblems(manifest));
    }

    [Fact]
    public void Missing_manifest_file_is_an_actionable_error()
    {
        var exception = Assert.Throws<PluginManifestException>(() => PluginManifestLoader.Load(_directory));
        Assert.Contains("no manifest.json", exception.Message);
        Assert.Contains(_directory, exception.Message);
    }

    [Fact]
    public void Malformed_json_is_an_actionable_error()
    {
        File.WriteAllText(Path.Combine(_directory, PluginManifest.FileName), "{ this is not json");
        var exception = Assert.Throws<PluginManifestException>(() => PluginManifestLoader.Load(_directory));
        Assert.Contains("not valid JSON", exception.Message);
    }

    [Fact]
    public void Unknown_schema_version_is_rejected()
    {
        var problems = PluginManifestValidator.DescribeProblems(FixtureManifest.V1() with { SchemaVersion = 2 });
        Assert.Contains(problems, problem => problem.Contains("schema version is 2", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Fixture@1")]
    [InlineData("fixture@0")]
    [InlineData("fixture")]
    [InlineData("fixture@two")]
    public void Invalid_provider_id_is_rejected(string id)
    {
        var problems = PluginManifestValidator.DescribeProblems(FixtureManifest.V1() with { Id = id });
        Assert.Contains(problems, problem => problem.Contains("not a valid provider id", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_capabilities_are_rejected()
    {
        var problems = PluginManifestValidator.DescribeProblems(FixtureManifest.V1() with { Capabilities = [] });
        Assert.Contains(problems, problem => problem.Contains("at least one capability", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_capability_ids_are_rejected()
    {
        var duplicates = FixtureManifest.V1Capabilities()
            .Concat([FixtureManifest.V1Capabilities()[0]]).ToArray();
        var problems = PluginManifestValidator.DescribeProblems(FixtureManifest.V1() with { Capabilities = duplicates });
        Assert.Contains(problems, problem => problem.Contains("declared more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_capability_fields_are_rejected()
    {
        var broken = FixtureManifest.Capability(
            "spatial.fixture.broken@1", string.Empty, string.Empty, string.Empty, ["cancellable"]) with
        {
            Errors = [],
        };
        var problems = PluginManifestValidator.DescribeProblems(
            FixtureManifest.V1() with { Capabilities = [broken] });
        Assert.Contains(problems, problem => problem.Contains("declare a purpose", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("input and output schemas", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("error variant", StringComparison.Ordinal));
    }

    [Fact]
    public void Bad_permission_and_error_codes_are_rejected()
    {
        var broken = FixtureManifest.V1Capabilities()[0] with
        {
            Permissions = ["Not Valid Permission"],
            Errors = [new ManifestError("not a valid code", string.Empty)],
        };
        var problems = PluginManifestValidator.DescribeProblems(
            FixtureManifest.V1() with { Capabilities = [broken] });
        Assert.Contains(problems, problem => problem.Contains("not a valid permission name", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("Not an error code", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_trait_is_rejected()
    {
        var broken = FixtureManifest.V1Capabilities()[0] with { Traits = ["explodes"] };
        var problems = PluginManifestValidator.DescribeProblems(
            FixtureManifest.V1() with { Capabilities = [broken] });
        Assert.Contains(problems, problem => problem.Contains("'explodes' is not a known trait", StringComparison.Ordinal));
    }

    [Fact]
    public void Long_running_requires_cancellable()
    {
        var broken = FixtureManifest.V1Capabilities()[0] with { Traits = ["long-running"] };
        var problems = PluginManifestValidator.DescribeProblems(
            FixtureManifest.V1() with { Capabilities = [broken] });
        Assert.Contains(problems, problem => problem.Contains("long-running but not cancellable", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_example_is_rejected()
    {
        var broken = FixtureManifest.V1Capabilities()[0] with
        {
            Examples = [new ManifestExample(string.Empty, string.Empty)],
        };
        var problems = PluginManifestValidator.DescribeProblems(
            FixtureManifest.V1() with { Capabilities = [broken] });
        Assert.Contains(problems, problem => problem.Contains("needs a name and a description", StringComparison.Ordinal));
    }

    [Fact]
    public void Unsupported_runtime_hint_is_rejected()
    {
        var problems = PluginManifestValidator.DescribeProblems(FixtureManifest.V1() with { Runtime = "wasm" });
        Assert.Contains(problems, problem => problem.Contains("not a supported runtime hint", StringComparison.Ordinal));
    }

    [Fact]
    public void TryLoad_reports_problems_without_throwing()
    {
        Assert.False(PluginManifestLoader.TryLoad(_directory, out var missing, out var missingProblems));
        Assert.Null(missing);
        Assert.NotEmpty(missingProblems);

        WriteManifest(FixtureManifest.V1());
        Assert.True(PluginManifestLoader.TryLoad(_directory, out var loaded, out var none));
        Assert.NotNull(loaded);
        Assert.Empty(none);
    }

    [Fact]
    public void Trait_map_round_trips_names()
    {
        Assert.Equal("cancellable", ManifestTraitMap.ToNames(CapabilityTraits.Cancellable).Single());
        Assert.Equal("streaming", ManifestTraitMap.ToNames(CapabilityTraits.Streaming).Single());
        Assert.Equal("long-running", ManifestTraitMap.ToNames(CapabilityTraits.LongRunning).Single());
        Assert.Equal(CapabilityTraits.LongRunning | CapabilityTraits.Cancellable,
            ManifestTraitMap.FromNames(["long-running", "cancellable"]));
        Assert.Null(ManifestTraitMap.FromName("explodes"));
        Assert.False(ManifestTraitMap.Contains(null));
    }

    [Fact]
    public void Compatible_provider_passes_the_activation_check()
    {
        var manifest = FixtureManifest.V1();
        var descriptors = manifest.Capabilities.Select(FixtureManifest.ToDescriptor).ToArray();

        Assert.Empty(ManifestCompatibility.DescribeProblems(manifest, ProviderId.Parse("fixture@1"), descriptors));
        ManifestCompatibility.EnsureCompatible(manifest, ProviderId.Parse("fixture@1"), descriptors);
    }

    [Fact]
    public void Compatibility_reports_every_divergence()
    {
        var manifest = FixtureManifest.V1();
        var descriptors = manifest.Capabilities.Select(FixtureManifest.ToDescriptor).ToArray();

        var diverged = descriptors[0] with
        {
            Purpose = "A different purpose.",
            Input = new SchemaDescriptor("other.shape"),
            Errors = [new ErrorVariant("other.error", "when")],
            RequiredPermissions = [Permission.Parse("spatial.fixture.write")],
            Traits = CapabilityTraits.SideEffects,
            Examples = [new ConformanceExample("other", "example")],
        };

        var extra = new CapabilityDescriptor(
            CapabilityId.Parse("spatial.fixture.extra@1"),
            "An extra capability.",
            new SchemaDescriptor("none"),
            new SchemaDescriptor("scalar"),
            [new ErrorVariant("invalid.arguments", "when")],
            [],
            CapabilityTraits.Cancellable,
            []);

        var problems = ManifestCompatibility.DescribeProblems(
            manifest with { Id = "fixture@2" }, ProviderId.Parse("fixture@1"), [diverged, descriptors[1], extra]);

        Assert.Contains(problems, problem => problem.Contains("provider id fixture@1 does not match", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("does not serve the manifest capability", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("purpose does not match", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("schema names do not match", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("error codes do not match", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("permissions do not match", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("traits do not match", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("example names do not match", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("serves spatial.fixture.extra@1, which the manifest does not declare", StringComparison.Ordinal));
    }

    [Fact]
    public void Compatibility_with_no_provider_descriptors_reports_the_declared_gap()
    {
        var manifest = FixtureManifest.V1();

        var problems = ManifestCompatibility.DescribeProblems(manifest, ProviderId.Parse("fixture@1"), []);

        var problem = Assert.Single(problems);
        Assert.Contains("declares no capabilities", problem);
        Assert.Contains("6", problem);
    }

    [Fact]
    public void Compatibility_skips_null_or_malformed_declared_capabilities()
    {
        var manifest = FixtureManifest.V1() with
        {
            // A JSON manifest may carry a null capability entry or one whose id
            // does not parse; both are skipped, they must not crash the check.
            Capabilities = [null!, FixtureManifest.Capability("no-such-version", "p", "none", "scalar")],
        };
        var served = FixtureManifest.ToDescriptor(FixtureManifest.V1Capabilities()[0]);

        var problems = ManifestCompatibility.DescribeProblems(manifest, ProviderId.Parse("fixture@1"), [served]);

        Assert.Contains(problems,
            problem => problem.Contains("serves spatial.fixture.peek@1, which the manifest does not declare", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compatibility_guards_its_null_inputs(bool nullManifest)
    {
        var manifest = FixtureManifest.V1();
        Assert.Throws<ArgumentNullException>(() => ManifestCompatibility.DescribeProblems(
            nullManifest ? null! : manifest,
            ProviderId.Parse("fixture@1"),
            nullManifest ? manifest.Capabilities.Select(FixtureManifest.ToDescriptor) : null!));
    }

    private void WriteManifest(PluginManifest manifest) =>
        File.WriteAllText(Path.Combine(_directory, PluginManifest.FileName), FixtureManifest.ToJson(manifest));
}
