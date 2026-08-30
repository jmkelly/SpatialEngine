using System.Reflection;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Transformations;
using static Spatial.Transformations.ProjNet.Tests.TransformInvoker;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// Provider-level contract tests: identity, descriptors, dispatch failure and
/// the ADR-0005 guardrail that no ProjNet type appears on the plugin's public
/// surface.
/// </summary>
public sealed class ProjNetProviderTests
{
    [Fact]
    public void The_provider_identity_is_projnet_at_one()
    {
        Assert.Equal("projnet@1", ProjNetTransformationsProvider.ProviderIdentifier.ToString());
        Assert.Equal(ProjNetTransformationsProvider.ProviderIdentifier, new ProjNetTransformationsProvider().Id);
    }

    [Fact]
    public void The_provider_registers_the_two_transformation_contract_descriptors()
    {
        var descriptors = new ProjNetTransformationsProvider().Descriptors;

        var describe = Assert.Single(descriptors, descriptor => descriptor.Id == CrsDescribeContract.Id);
        Assert.Equal("spatial.crs.describe@1", describe.Id.ToString());
        Assert.Equal("crs.identity", describe.Input.Name);
        Assert.Equal("crs.description", describe.Output.Name);
        Assert.Equal(CapabilityTraits.Cancellable, describe.Traits);
        Assert.Single(describe.Errors);
        Assert.Equal("invalid.arguments", describe.Errors[0].Code);
        Assert.Equal(4, describe.Examples.Count);

        var transform = Assert.Single(descriptors, descriptor => descriptor.Id == TransformContract.Id);
        Assert.Equal("spatial.coordinate.transform@1", transform.Id.ToString());
        Assert.Equal("geometry.transform", transform.Input.Name);
        Assert.Equal("geometry", transform.Output.Name);
        Assert.Equal(CapabilityTraits.Cancellable, transform.Traits);
        // The transform contract's geometry-carrying fixtures ship with the
        // conformance suite (ADR-0027 §conformance fixtures); the descriptor
        // deliberately declares none.
        Assert.Empty(transform.Examples);
    }

    [Fact]
    public async Task An_unknown_capability_is_a_contract_violation()
    {
        var result = await InvokeAsync(
            CapabilityId.Parse("spatial.coordinate.unknown@1"),
            new Dictionary<string, object?>());

        var failure = Assert.IsType<CapabilityFailure>(result);
        Assert.Equal(CapabilityErrorKind.ContractViolation, failure.Error.Kind);
        Assert.Contains("does not serve", failure.Error.Message);
    }

    [Fact]
    public void No_ProjNet_type_appears_on_the_public_surface()
    {
        // ADR-0005: third-party spatial types must never cross a public
        // boundary. Every public type's public members must be core/SDK/BCL
        // types only.
        var violations = new List<string>();
        var assembly = typeof(ProjNetTransformationsProvider).Assembly;
        foreach (var type in assembly.GetExportedTypes())
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var signatureTypes = SignatureTypes(member).Concat([member.DeclaringType!]);
                foreach (var candidate in signatureTypes)
                {
                    if (NamesProjNet(candidate))
                    {
                        violations.Add($"{type.FullName}.{member.Name} exposes {candidate.FullName}.");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    private static IEnumerable<Type> SignatureTypes(MemberInfo member) => member switch
    {
        ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
        MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType)
            .Append(method.ReturnType),
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        _ => [],
    };

    private static bool NamesProjNet(Type type)
    {
        for (var current = type; current is not null; current = current.DeclaringType)
        {
            if (current.Namespace?.StartsWith("ProjNet.", StringComparison.Ordinal) == true
                || current.Namespace == "ProjNet")
            {
                return true;
            }
        }

        return false;
    }
}
