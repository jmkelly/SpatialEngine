using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginHost.DotNet.Manifest;

/// <summary>
/// Validates a <see cref="PluginManifest"/> against the manifest schema
/// (<c>architecture/plugin-manifest.md</c>): identity, runtime hint, loadable
/// payload and every capability contract rule. Mirrors the capability
/// registration rules (<see cref="Spatial.Runtime.Capabilities.CapabilityRegistry"/>)
/// plus the packaging rules unique to manifests — one package, one provider
/// id, a known runtime hint, a loadable assembly and no duplicate capability
/// ids. Problems are actionable statements, never a single opaque failure.
/// </summary>
public static class PluginManifestValidator
{
    /// <summary>Lists every problem with <paramref name="manifest"/>, or an empty list when it is valid.</summary>
    public static IReadOnlyList<string> DescribeProblems(PluginManifest manifest)
    {
        if (manifest is null)
        {
            return ["The manifest is null."];
        }

        var problems = new List<string>();
        ValidateSchemaVersion(manifest, problems);
        ValidateIdentity(manifest, problems);
        ValidatePayload(manifest, problems);
        ValidateCapabilities(manifest, problems);
        return problems;
    }

    /// <summary>Throws <see cref="PluginManifestException"/> listing every problem when the manifest is invalid.</summary>
    public static void EnsureValid(PluginManifest manifest)
    {
        var problems = DescribeProblems(manifest);
        if (problems.Count > 0)
        {
            throw new PluginManifestException($"The plugin manifest is invalid: {string.Join("; ", problems)}");
        }
    }

    private static void ValidateSchemaVersion(PluginManifest manifest, List<string> problems)
    {
        if (manifest.SchemaVersion != PluginManifest.SchemaVersionV1)
        {
            problems.Add(
                $"the schema version is {manifest.SchemaVersion}, but this host understands version "
                + $"{PluginManifest.SchemaVersionV1} only");
        }
    }

    private static void ValidateIdentity(PluginManifest manifest, List<string> problems)
    {
        if (!ProviderId.TryParse(manifest.Id, out _))
        {
            problems.Add($"'{manifest.Id}' is not a valid provider id: expected 'name@version' with a dotted lowercase name");
        }

        if (string.IsNullOrWhiteSpace(manifest.DisplayName))
        {
            problems.Add("the display name must not be empty");
        }
    }

    private static void ValidatePayload(PluginManifest manifest, List<string> problems)
    {
        if (manifest.Runtime != "dotnet")
        {
            problems.Add($"'{manifest.Runtime}' is not a supported runtime hint; this host supports 'dotnet' packages only");
        }

        if (string.IsNullOrWhiteSpace(manifest.Assembly))
        {
            problems.Add("the assembly (the loadable payload for the 'dotnet' runtime hint) must not be empty");
        }
    }

    private static void ValidateCapabilities(PluginManifest manifest, List<string> problems)
    {
        if (manifest.Capabilities is null || manifest.Capabilities.Count == 0)
        {
            problems.Add("a plugin package must declare at least one capability");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in manifest.Capabilities)
        {
            ValidateCapability(capability, seen, problems);
        }
    }

    private static void ValidateCapability(ManifestCapability capability, HashSet<string> seen, List<string> problems)
    {
        var prefix = "capability";
        if (capability is null)
        {
            problems.Add("a capability entry is null");
            return;
        }

        if (!CapabilityId.TryParse(capability.Id, out _))
        {
            problems.Add($"'{capability.Id}' is not a valid capability id: expected 'name@version' with a dotted lowercase name");
        }
        else if (!seen.Add(capability.Id))
        {
            problems.Add($"{prefix} '{capability.Id}' is declared more than once");
        }

        if (string.IsNullOrWhiteSpace(capability.Purpose))
        {
            problems.Add($"{prefix} '{capability.Id}' must declare a purpose");
        }

        if (string.IsNullOrWhiteSpace(capability.InputSchema) || string.IsNullOrWhiteSpace(capability.OutputSchema))
        {
            problems.Add($"{prefix} '{capability.Id}' must declare named input and output schemas");
        }

        ValidateErrors(capability, problems);
        ValidatePermissions(capability, problems);
        ValidateTraits(capability, problems);
        ValidateExamples(capability, problems);
    }

    private static void ValidateErrors(ManifestCapability capability, List<string> problems)
    {
        if (capability.Errors is null || capability.Errors.Count == 0)
        {
            problems.Add($"capability '{capability.Id}' must declare at least one error variant (plan §9)");
            return;
        }

        foreach (var error in capability.Errors)
        {
            try
            {
                _ = new ErrorVariant(error.Code, error.Description);
            }
            catch (ArgumentException exception)
            {
                problems.Add($"capability '{capability.Id}': error variant {exception.Message}");
            }
        }
    }

    private static void ValidatePermissions(ManifestCapability capability, List<string> problems)
    {
        foreach (var name in capability.Permissions ?? [])
        {
            if (!Permission.TryParse(name, out _))
            {
                problems.Add($"capability '{capability.Id}': '{name}' is not a valid permission name");
            }
        }
    }

    private static void ValidateTraits(ManifestCapability capability, List<string> problems)
    {
        foreach (var name in capability.Traits ?? [])
        {
            if (!ManifestTraitMap.Contains(name))
            {
                problems.Add(
                    $"capability '{capability.Id}': '{name}' is not a known trait; "
                    + $"known traits: {string.Join(", ", ManifestTraitMap.Names)}");
            }
        }

        var traits = ManifestTraitMap.FromNames(capability.Traits ?? []);
        if ((traits & CapabilityTraits.LongRunning) != 0 && (traits & CapabilityTraits.Cancellable) == 0)
        {
            problems.Add(
                $"capability '{capability.Id}' is long-running but not cancellable; "
                + "long-running capabilities are always cancellable (ADR-0008)");
        }
    }

    private static void ValidateExamples(ManifestCapability capability, List<string> problems)
    {
        foreach (var example in capability.Examples ?? [])
        {
            if (string.IsNullOrWhiteSpace(example.Name) || string.IsNullOrWhiteSpace(example.Description))
            {
                problems.Add($"capability '{capability.Id}': a conformance example needs a name and a description");
            }
        }
    }
}
