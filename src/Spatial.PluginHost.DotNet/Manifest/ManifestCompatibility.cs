using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginHost.DotNet.Manifest;

/// <summary>
/// The health/activation compatibility check (plan §10.4 "run health and
/// compatibility checks"): verifies that a loaded plugin provider's actual
/// contract surface matches the package's approved manifest before the
/// package goes live — provider id, and for every manifest capability the
/// capability id, purpose, interchange schema names, error codes, required
/// permissions, traits and conformance example names. Prose (schema
/// descriptions, error descriptions) may differ; the contract *identity*
/// must not. The supervisor runs this after the worker's handshake and the
/// worker host runs it before announcing readiness, so an out-of-date
/// manifest can never accidentally route traffic to a changed contract.
/// </summary>
public static class ManifestCompatibility
{
    /// <summary>Lists every incompatibility, or an empty list when the provider matches the manifest.</summary>
    public static IReadOnlyList<string> DescribeProblems(
        PluginManifest manifest,
        ProviderId providerId,
        IEnumerable<CapabilityDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(descriptors);

        var problems = new List<string>();
        var actual = descriptors.ToArray();
        if (actual.Length == 0)
        {
            problems.Add(
                $"the provider for {manifest.Id} declares no capabilities, but the manifest declares "
                + $"{manifest.Capabilities.Count}");
            return problems;
        }

        if (providerId.ToString() != manifest.Id)
        {
            problems.Add($"the provider id {providerId} does not match the manifest id {manifest.Id}");
        }

        var declaredIds = (manifest.Capabilities ?? []).Where(capability => capability is not null)
            .Select(capability => capability!.Id).ToHashSet(StringComparer.Ordinal);
        var byCapability = actual.ToDictionary(descriptor => descriptor.Id, descriptor => descriptor);
        foreach (var declared in manifest.Capabilities ?? [])
        {
            if (declared is null || !CapabilityId.TryParse(declared.Id, out var id))
            {
                continue;
            }

            if (!byCapability.TryGetValue(id, out var descriptor))
            {
                problems.Add($"the provider does not serve the manifest capability {declared.Id}");
                continue;
            }

            CompareFields(declared, descriptor, problems);
        }

        foreach (var served in byCapability.Keys.Where(id => !declaredIds.Contains(id.ToString())))
        {
            problems.Add($"the provider serves {served}, which the manifest does not declare");
        }

        return problems;
    }

    /// <summary>Throws <see cref="PluginManifestException"/> when the provider surface diverges from the manifest.</summary>
    public static void EnsureCompatible(PluginManifest manifest, ProviderId providerId, IEnumerable<CapabilityDescriptor> descriptors)
    {
        var problems = DescribeProblems(manifest, providerId, descriptors);
        if (problems.Count > 0)
        {
            throw new PluginManifestException(
                $"The loaded provider is not compatible with the package manifest: {string.Join("; ", problems)}");
        }
    }

    private static void CompareFields(ManifestCapability declared, CapabilityDescriptor descriptor, List<string> problems)
    {
        var at = $"capability '{declared.Id}'";
        if (!string.Equals(declared.Purpose, descriptor.Purpose, StringComparison.Ordinal))
        {
            problems.Add($"{at}: the manifest purpose does not match the provider's purpose");
        }

        if (declared.InputSchema != descriptor.Input.Name || declared.OutputSchema != descriptor.Output.Name)
        {
            problems.Add($"{at}: the manifest schema names do not match the provider's schemas");
        }

        var declaredErrors = (declared.Errors ?? []).Select(error => error.Code).ToHashSet(StringComparer.Ordinal);
        var actualErrors = descriptor.Errors.Select(error => error.Code).ToHashSet(StringComparer.Ordinal);
        if (!declaredErrors.SetEquals(actualErrors))
        {
            problems.Add($"{at}: the manifest error codes do not match the provider's error codes");
        }

        var declaredPermissions = (declared.Permissions ?? []).ToHashSet(StringComparer.Ordinal);
        var actualPermissions = descriptor.RequiredPermissions.Select(permission => permission.Name).ToHashSet(StringComparer.Ordinal);
        if (!declaredPermissions.SetEquals(actualPermissions))
        {
            problems.Add($"{at}: the manifest permissions do not match the provider's permissions");
        }

        var declaredTraits = (declared.Traits ?? []).ToHashSet(StringComparer.Ordinal);
        var actualTraits = ManifestTraitMap.ToNames(descriptor.Traits).ToHashSet(StringComparer.Ordinal);
        if (!declaredTraits.SetEquals(actualTraits))
        {
            problems.Add($"{at}: the manifest traits do not match the provider's traits");
        }

        var declaredExamples = (declared.Examples ?? []).Select(example => example.Name).ToHashSet(StringComparer.Ordinal);
        var actualExamples = descriptor.Examples.Select(example => example.Name).ToHashSet(StringComparer.Ordinal);
        if (!declaredExamples.SetEquals(actualExamples))
        {
            problems.Add($"{at}: the manifest example names do not match the provider's examples");
        }
    }
}
