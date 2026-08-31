using Spatial.PluginHost.DotNet.Manifest;
using Spatial.PluginHost.DotNet.Supervision;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.Provider.PostGIS;
using Spatial.Runtime.Capabilities;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The worker-mode integration test (ADR-0006/0025/0028): the packaged
/// <c>postgis@1</c> worker launched by the supervisor with the container's
/// connection string in its launch environment (host-managed secrets) serves
/// real data over the wire protocol — the strongest proof that secrets reach
/// the provider at launch and that streams/values cross correctly.
/// </summary>
public sealed class PostgisWorkerIntegrationTests : IClassFixture<PostgisContainerFixture>
{
    private readonly PostgisContainerFixture _fixture;

    public PostgisWorkerIntegrationTests(PostgisContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task A_seeded_worker_streams_the_catalogue_over_the_wire()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");

        var root = Directory.CreateTempSubdirectory("spatial-postgis-worker-").FullName;
        var package = WritePackage(root);
        var runtime = new CapabilityRuntime(new CapabilityRegistry());
        var supervisor = new WorkerSupervisor(
            runtime,
            new WorkerSupervisorOptions(
                health: new WorkerHealthOptions(
                    PingIntervalMilliseconds: 5000,
                    PingTimeoutMilliseconds: 1000,
                    FailureThreshold: 10),
                startupTimeout: TimeSpan.FromSeconds(20),
                drainTimeout: TimeSpan.FromSeconds(15),
                workerEnvironment: new Dictionary<string, string>
                {
                    [Spatial.Provider.PostGIS.Configuration.PostgisConnectionConfiguration.EnvironmentVariable] = _fixture.ConnectionString,
                }));
        try
        {
            await supervisor.ActivateAsync(package);

            var outcome = await runtime.InvokeAsync(CapabilityInvocation.Create(
                CatalogueListContract.Id,
                new Dictionary<string, object?>()));

            Assert.True(outcome.Result.IsSuccess, $"worker invocation failed: {outcome.Error}");
            Assert.True(outcome.TryGetValue(out var value), "expected a stream handle");
            var handle = Assert.IsType<Spatial.PluginSdk.Resources.ResourceHandle>(value);
            Assert.True(runtime.Resources.TryAcquireLease(handle, null, out var lease));
            Assert.True(runtime.Resources.TryOpenStream(handle, lease, out var stream));
            try
            {
                var items = new List<string>();
                while (true)
                {
                    var batch = await stream.ReadBatchAsync(16);
                    if (batch.Count == 0)
                    {
                        break;
                    }

                    items.AddRange(batch.Cast<string>());
                }

                Assert.Contains("public.places", items.Select(DatasetMetadataJson.ReadSummary).Select(summary => summary.Id));
            }
            finally
            {
                Assert.True(runtime.Resources.ReleaseLease(lease));
                await runtime.Resources.CloseAsync(handle);
            }
        }
        finally
        {
            await supervisor.DisposeAsync();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string WritePackage(string root)
    {
        var fixture = new PostgisManifestFixture();
        var directory = Path.Combine(root, fixture.Manifest.Id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PluginManifest.FileName), fixture.ToJson());

        var pluginDirectory = Path.GetDirectoryName(typeof(PostgisProvider).Assembly.Location)!;
        foreach (var (source, name) in new[]
        {
            (typeof(PostgisProvider).Assembly.Location, "Spatial.Provider.PostGIS.dll"),
            (typeof(Spatial.Core.Features.FeatureId).Assembly.Location, "Spatial.Core.dll"),
            (typeof(Spatial.PluginSdk.Capabilities.ICapabilityProvider).Assembly.Location, "Spatial.PluginSdk.dll"),
            (Path.Combine(pluginDirectory, "Npgsql.dll"), "Npgsql.dll"),
            (Path.Combine(pluginDirectory, "Microsoft.Extensions.Logging.Abstractions.dll"), "Microsoft.Extensions.Logging.Abstractions.dll"),
        })
        {
            File.Copy(source, Path.Combine(directory, name));
        }

        return directory;
    }

    private sealed class PostgisManifestFixture
    {
        public PostgisManifestFixture()
        {
            var provider = new PostgisProvider();
            var capabilities = provider.Descriptors.Select(ToManifest).ToArray();
            Manifest = new PluginManifest(
                PluginManifest.SchemaVersionV1,
                provider.Id.ToString(),
                "PostGIS data provider",
                "dotnet",
                "Spatial.Provider.PostGIS.dll",
                "Spatial.Provider.PostGIS.PostgisProvider",
                capabilities);
        }

        public PluginManifest Manifest { get; }

        private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        };

        public string ToJson() => System.Text.Json.JsonSerializer.Serialize(new
        {
            schemaVersion = Manifest.SchemaVersion,
            id = Manifest.Id,
            displayName = Manifest.DisplayName,
            runtime = Manifest.Runtime,
            assembly = Manifest.Assembly,
            assemblyType = Manifest.AssemblyType,
            capabilities = Manifest.Capabilities.Select(ToJson).ToArray(),
        }, JsonOptions);

        private static ManifestCapability ToManifest(CapabilityDescriptor descriptor) =>
            new(
                descriptor.Id.ToString(),
                descriptor.Purpose,
                descriptor.Input.Name,
                descriptor.Output.Name,
                descriptor.Errors.Select(error => new ManifestError(error.Code, error.Description)).ToArray(),
                descriptor.RequiredPermissions.Select(permission => permission.Name).ToArray(),
                ManifestTraitMap.ToNames(descriptor.Traits).ToArray(),
                descriptor.Examples.Select(example => new ManifestExample(example.Name, example.Description)).ToArray());

        private static object ToJson(ManifestCapability capability) => new
        {
            id = capability.Id,
            purpose = capability.Purpose,
            inputSchema = capability.InputSchema,
            outputSchema = capability.OutputSchema,
            errors = capability.Errors.Select(error => new { code = error.Code, description = error.Description }).ToArray(),
            permissions = capability.Permissions,
            traits = capability.Traits,
            examples = capability.Examples.Select(example => new { name = example.Name, description = example.Description }).ToArray(),
        };
    }
}
