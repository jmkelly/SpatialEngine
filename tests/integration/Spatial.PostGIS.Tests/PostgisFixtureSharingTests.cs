using System.Reflection;
using Xunit;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// How the suite acquires its container (ADR-0010/0028's degradation, as
/// SpatialEngine-o5p found it). Every container-backed test class asks for a
/// database inside the same shared fixture, and nothing in the suite starts a
/// container of its own: a class fixture starts a PostGIS container per class,
/// and fifteen of them starting at once under a loaded lane is what turned the
/// suite's honest skip into a run that reported most of itself as skipped while
/// saying nothing about why (ADR-0139).
/// </summary>
public sealed class PostgisFixtureSharingTests
{
    private static readonly Assembly Suite = typeof(PostgisFixtureSharingTests).Assembly;

    /// <summary>
    /// The container-backed test classes: those that hold a fact and take a
    /// container fixture. The fixtures themselves hold neither, which is what
    /// keeps the set to test classes.
    /// </summary>
    private static Type[] ContainerBackedTestClasses() =>
    [
        .. Suite.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(type => type.GetMethods().Any(HoldsAFact))
            .Where(type => type.GetConstructors().Any(constructor =>
                constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(PostgisDatabaseFixture)
                    || parameter.ParameterType == typeof(PostgisContainerFixture))))
    ];

    private static bool HoldsAFact(MethodInfo method) =>
        method.GetCustomAttributesData()
            .Any(attribute => attribute.AttributeType.Name is "FactAttribute" or "SkippableFactAttribute" or "TheoryAttribute" or "SkippableTheoryAttribute");

    /// <summary>
    /// The collections whose fixture is the container: the definitions that
    /// hand the one container to every class that asks for it.
    /// </summary>
    private static HashSet<string> SharedContainerCollections() =>
    [
        .. Suite.GetTypes()
            .Where(type => typeof(ICollectionFixture<PostgisContainerFixture>).IsAssignableFrom(type))
            .SelectMany(type => CustomAttributes(type, typeof(CollectionDefinitionAttribute)))
            .Select(CollectionName)
    ];

    /// <summary>
    /// The attributes of that exact type on a type — xunit reads a collection's
    /// name out of the attribute's constructor rather than a property, so the
    /// arguments are what a reader needs.
    /// </summary>
    private static IEnumerable<CustomAttributeData> CustomAttributes(Type type, Type attributeType) =>
        [.. type.GetCustomAttributesData().Where(attribute => attribute.AttributeType == attributeType)];

    private static string CollectionName(CustomAttributeData attribute) =>
        Assert.IsType<string>(attribute.ConstructorArguments[0].Value);

    [Fact]
    public void The_container_backed_classes_are_the_ones_the_suite_claims()
    {
        // Guards the reflection above from passing vacuously: a suite that
        // stopped taking the fixture would otherwise make the two tests below
        // trivially true.
        Assert.True(
            ContainerBackedTestClasses().Length >= 10,
            $"expected the PostGIS suite's container-backed classes to be found, found {ContainerBackedTestClasses().Length}");
    }

    [Fact]
    public void No_test_class_declares_the_container_as_its_own_class_fixture()
    {
        var perClass = new List<string>();
        foreach (var type in ContainerBackedTestClasses())
        {
            if (typeof(IClassFixture<PostgisContainerFixture>).IsAssignableFrom(type))
            {
                perClass.Add(type.Name);
            }
        }

        Assert.Empty(perClass);
    }

    [Fact]
    public void Every_container_backed_class_takes_the_fixture_from_the_one_shared_collection()
    {
        var shared = SharedContainerCollections();
        Assert.Single(shared);

        foreach (var type in ContainerBackedTestClasses())
        {
            var collection = Assert.Single(CustomAttributes(type, typeof(CollectionAttribute)));
            Assert.Contains(CollectionName(collection), shared);
        }
    }
}
