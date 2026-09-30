using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;

namespace Spatial.Architecture.Tests;

/// <summary>
/// The schema wire shape's binding hazards, kept in code rather than in prose
/// (ADR-0033). <c>System.Text.Json</c> cannot bind either core schema type on
/// its own, and it cannot deserialize an interface-typed property at all, so
/// both declarations fail silently or at a distance:
///
/// <list type="bullet">
/// <item><see cref="FeatureSchema"/> has no parameterless constructor and a
/// read-only <c>Fields</c> property, so an unregistered converter makes STJ
/// throw <see cref="InvalidOperationException"/> from whichever unrelated
/// call first resolves the contract's metadata.</item>
/// <item><see cref="FieldDefinition"/> is an immutable struct whose single
/// constructor is never chosen, so an unregistered converter yields
/// <c>default</c> field definitions — no exception, an empty-looking schema.</item>
/// <item>Declaring <c>DatasetDescription.Schema</c> as
/// <see cref="IFeatureSchema"/> makes every catalogue round trip throw
/// <see cref="System.Text.Json.NotSupportedException"/>.</item>
/// </list>
///
/// So: the converters must stay registered on
/// <see cref="HostApiJson.Options"/>, and the description's schema property
/// must stay the concrete <see cref="FeatureSchema"/>. The functional round
/// trip these two rules protect lives in
/// <c>tests/unit/Spatial.Client.Tests</c> (ADR-0069 keeps converter
/// behaviour out of this structural suite).
/// </summary>
public sealed class SchemaBindingGuardTests
{
    /// <summary>
    /// The description's schema property must be a concrete type STJ can
    /// instantiate. An interface-typed refactor is otherwise an innocuous-looking
    /// change that breaks the catalogue round trip at runtime.
    /// </summary>
    [Fact]
    public void Dataset_description_schema_property_stays_concrete()
    {
        var property = typeof(DatasetDescription).GetProperty(nameof(DatasetDescription.Schema))
            ?? throw new InvalidOperationException(
                "DatasetDescription no longer declares a Schema property; update this guard with the new shape.");

        Assert.False(
            property.PropertyType.IsInterface || property.PropertyType.IsAbstract,
            $"DatasetDescription.Schema is declared {property.PropertyType.Name}. System.Text.Json cannot " +
            "deserialize an interface or abstract type, so every catalogue round trip throws " +
            "NotSupportedException. Declare the concrete FeatureSchema and accept the interface only where " +
            "the type is never deserialized.");

        Assert.Equal(typeof(FeatureSchema), property.PropertyType);
    }

    /// <summary>
    /// The shared host API options must resolve the explicit converters for both
    /// core schema types. Removing one is not a compile error: STJ binds
    /// <see cref="FieldDefinition"/> to defaults in silence and rejects
    /// <see cref="FeatureSchema"/> outright.
    /// </summary>
    [Theory]
    [InlineData(typeof(FeatureSchema), typeof(FeatureSchemaConverter))]
    [InlineData(typeof(FieldDefinition), typeof(FieldDefinitionConverter))]
    public void Host_api_options_resolve_the_explicit_schema_converters(Type schemaType, Type converterType)
    {
        var converter = HostApiJson.Options.GetConverter(schemaType);

        Assert.True(
            converterType.IsInstanceOfType(converter),
            $"{HostApiJson.Options.GetType().Name} resolves {schemaType.Name} to " +
            $"{converter.GetType().Name} rather than {converterType.Name}. Without the explicit converter the " +
            "typed API cannot bind the core schema type: FieldDefinition deserializes to default values in " +
            "silence and FeatureSchema throws. The functional round trip is covered by the client SDK suite.");
    }

    /// <summary>
    /// Belt-and-braces: the converters are registered as instances rather than
    /// discovered by convention, so a converter renamed out of
    /// <see cref="HostApiJson"/>'s collection is caught here even if
    /// <see cref="JsonSerializerOptions.GetConverter"/> can still satisfy the
    /// lookup through a stale attribute.
    /// </summary>
    [Fact]
    public void Host_api_options_carry_the_schema_converters_by_instance()
    {
        var missing = new[] { typeof(FeatureSchemaConverter), typeof(FieldDefinitionConverter) }
            .Where(expected => !HostApiJson.Options.Converters.Any(c => c.GetType() == expected))
            .Select(c => c.Name)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"HostApiJson.Options does not register {string.Join(", ", missing)}. Both converters are " +
            "load-bearing on the typed API wire and neither is discoverable by convention.");
    }
}
