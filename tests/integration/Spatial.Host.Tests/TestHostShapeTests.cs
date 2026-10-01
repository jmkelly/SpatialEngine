namespace Spatial.Host.Tests;

/// <summary>
/// The suite's per-class host gate: a class the shared-host register names
/// must actually boot one host for the class (ADR-0160).
/// </summary>
/// <remarks>
/// ADR-0155 declined one host per class on the whole-suite extrapolation and
/// asked for the audit instead, and the audit came back positive for the OGC
/// boundary class: one host holds fifty-three datasets cheaply, and no OGC
/// test's assertion depends on its own map being the only map. The lever is
/// worth its audit there, and the conversion is held here — a converted class
/// that quietly goes back to <c>new SomeFactory()</c> in a test body is the
/// shape ADR-0154 measured, and this is what notices.
/// </remarks>
public sealed class TestHostShapeTests
{
    [Fact]
    public void The_registered_classes_boot_one_host_for_the_whole_class()
    {
        var converted = typeof(TestHostShapeTests).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && SharedHostPolicy.IsConverted(type))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(converted);

        var broken = converted
            .Select(type => SharedHostPolicy.WhyNotOneHostPerClass(type))
            .Where(reason => reason is not null)
            .ToList();

        Assert.True(
            broken.Count == 0,
            "these classes are registered as one host per class and are not: "
            + string.Join(" | ", broken));
    }

    [Fact]
    public void The_register_names_classes_that_exist()
    {
        var testClasses = typeof(TestHostShapeTests).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract)
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = SharedHostPolicy.Converted
            .Where(name => !testClasses.Contains(name))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "the shared-host register names classes this assembly does not have: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void Every_class_that_boots_its_own_host_has_an_audited_verdict()
    {
        var unaudited = SharedHostPolicy.ClassesBootingTheirOwnHost()
            .Where(type => SharedHostPolicy.VerdictFor(type) is null)
            .Select(type => type.Name)
            .ToList();

        Assert.True(
            unaudited.Count == 0,
            "these classes boot a host per test and ADR-0161 has no verdict for them, so nobody has "
            + "asked whether the host could be the class's: " + string.Join(", ", unaudited));
    }

    [Fact]
    public void A_convert_verdict_is_the_conversion_register_and_nothing_else()
    {
        var converted = SharedHostPolicy.Audited
            .Where(entry => entry.Value.Verdict == SharedHostPolicy.HostVerdict.Convert)
            .Select(entry => entry.Key)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(converted, SharedHostPolicy.Converted.OrderBy(name => name, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Every_audited_class_exists_and_carries_its_evidence()
    {
        var testClasses = typeof(TestHostShapeTests).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract)
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = SharedHostPolicy.Audited.Keys.Where(name => !testClasses.Contains(name)).ToList();
        var silent = SharedHostPolicy.Audited
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value.Evidence))
            .Select(entry => entry.Key)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "the ADR-0161 audit names classes this assembly does not have: " + string.Join(", ", missing));
        Assert.True(
            silent.Count == 0,
            "these classes have a verdict but no evidence for it, which is an opinion rather than an "
            + "audit: " + string.Join(", ", silent));
    }

    [Fact]
    public void A_class_that_constructs_its_own_factory_is_named_as_the_reason()
    {
        // The read the gate is built on, checked against a class that is
        // deliberately not on the register: a per-test host is invisible to a
        // reviewer and has to be caught by the policy, not by a code comment.
        var perTest = typeof(TestHostShapeTests).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && !SharedHostPolicy.IsConverted(type))
            .Select(type => SharedHostPolicy.WhyNotOneHostPerClass(type))
            .ToList();

        Assert.All(perTest, Assert.Null);
    }
}
