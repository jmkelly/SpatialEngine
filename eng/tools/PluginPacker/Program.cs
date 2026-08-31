using System.Text.Json;
using Spatial.Operations.NetTopologySuite;
using Spatial.PluginHost.DotNet.Manifest;
using Spatial.PluginSdk.Capabilities;
using Spatial.Provider.PostGIS;

namespace Spatial.PluginPacker;

/// <summary>
/// Writes immutable plugin worker packages into a packages root (one
/// directory per package id, each with manifest.json plus the provider
/// assembly, its contracts, Core/PluginSdk and any runtime dependencies) —
/// the deployable input to <c>Spatial.Host</c>'s <c>Spatial:PackagesRoot</c>.
/// Manifests are derived from the providers' own contract descriptors, so the
/// package surface can never drift from the contracts they implement (the
/// worker host's activation compatibility check passes by construction).
///
/// Usage: <c>PluginPacker --packages-root &lt;dir&gt; [--only nts|postgis]</c>
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static int Main(string[] args)
    {
        var packagesRoot = ReadArgument(args, "--packages-root");
        if (packagesRoot is null)
        {
            Console.Error.WriteLine("usage: PluginPacker --packages-root <dir> [--only nts|postgis]");
            return 2;
        }

        var only = ReadArgument(args, "--only");
        var written = 0;
        if (only is null or "nts")
        {
            var provider = new NtsOperationsProvider();
            var ntsDirectory = Path.GetDirectoryName(typeof(NetTopologySuite.Geometries.Geometry).Assembly.Location)!;
            WritePackage(
                packagesRoot,
                provider,
                "Spatial.Operations.NetTopologySuite.dll",
                "Spatial.Operations.NetTopologySuite.NtsOperationsProvider",
                "NetTopologySuite operations",
                [(Path.Combine(ntsDirectory, "NetTopologySuite.dll"), "NetTopologySuite.dll")]);
            written++;
        }

        if (only is null or "postgis")
        {
            var provider = new PostgisProvider();
            var directory = Path.GetDirectoryName(typeof(PostgisProvider).Assembly.Location)!;
            WritePackage(
                packagesRoot,
                provider,
                "Spatial.Provider.PostGIS.dll",
                "Spatial.Provider.PostGIS.PostgisProvider",
                "PostGIS provider",
                [
                    (Path.Combine(directory, "Npgsql.dll"), "Npgsql.dll"),
                    (Path.Combine(directory, "Microsoft.Extensions.Logging.Abstractions.dll"), "Microsoft.Extensions.Logging.Abstractions.dll"),
                ]);
            written++;
        }

        Console.WriteLine($"packed {written} plugin package(s) into {packagesRoot}");
        return 0;
    }

    private static void WritePackage(
        string packagesRoot,
        ICapabilityProvider provider,
        string providerAssembly,
        string providerType,
        string displayName,
        IReadOnlyList<(string Source, string Name)> dependencies)
    {
        var manifest = BuildManifest(provider, providerAssembly, providerType, displayName);
        var directory = Path.Combine(packagesRoot, manifest.Id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PluginManifest.FileName), ToJson(manifest));

        foreach (var (source, name) in new List<(string, string)>
        {
            (provider.GetType().Assembly.Location, providerAssembly),
            (typeof(Spatial.Core.Geometry.IGeometry).Assembly.Location, "Spatial.Core.dll"),
            (typeof(ICapabilityProvider).Assembly.Location, "Spatial.PluginSdk.dll"),
        }.Concat(dependencies.Select(dependency => (dependency.Source, dependency.Name))))
        {
            File.Copy(source, Path.Combine(directory, name), overwrite: true);
        }
    }

    private static PluginManifest BuildManifest(ICapabilityProvider provider, string assembly, string assemblyType, string displayName)
    {
        var capabilities = provider.Descriptors.Select(ToManifestCapability).ToArray();
        return new PluginManifest(
            PluginManifest.SchemaVersionV1,
            provider.Id.ToString(),
            displayName,
            "dotnet",
            assembly,
            assemblyType,
            capabilities);
    }

    private static ManifestCapability ToManifestCapability(CapabilityDescriptor descriptor) =>
        new(
            descriptor.Id.ToString(),
            descriptor.Purpose,
            descriptor.Input.Name,
            descriptor.Output.Name,
            descriptor.Errors.Select(error => new ManifestError(error.Code, error.Description)).ToArray(),
            descriptor.RequiredPermissions.Select(permission => permission.Name).ToArray(),
            ManifestTraitMap.ToNames(descriptor.Traits).ToArray(),
            []);

    private static string ToJson(PluginManifest manifest)
    {
        var document = new
        {
            schemaVersion = manifest.SchemaVersion,
            id = manifest.Id,
            displayName = manifest.DisplayName,
            runtime = manifest.Runtime,
            assembly = manifest.Assembly,
            assemblyType = manifest.AssemblyType,
            capabilities = manifest.Capabilities.Select(ToJson).ToArray(),
        };
        return JsonSerializer.Serialize(document, JsonOptions);
    }

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

    private static string? ReadArgument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}