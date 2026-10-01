using System.Reflection;
using System.Reflection.Emit;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// The suite's per-class host policy: which classes boot one host for the
/// whole class rather than one per test, and the shape that makes a shared
/// host safe (ADR-0154, ADR-0155, ADR-0160).
/// </summary>
/// <remarks>
/// <para>A <see cref="WebApplicationFactory{TEntryPoint}"/> boot costs 0.55 s
/// once the process is warm (ADR-0154), and the suite constructs a factory at
/// 184 sites, so the per-test host is ~100 s of a summed run for a host every
/// test in a class would share. ADR-0155 declined the lever on the whole-suite
/// extrapolation and asked for the audit instead: can one host hold N datasets
/// cheaply, and does any test's assertion depend on its own dataset being the
/// only one?</para>
///
/// <para>The answer ADR-0160 records is that it can, under one rule — <b>one
/// map name per test</b> — and this class holds the register of the classes
/// that have been converted, so the conversion is a list rather than a
/// convention nobody can check.</para>
///
/// <para>A class on the list is expected to be an
/// <c>IClassFixture&lt;T&gt;</c> whose fixture derives from
/// <see cref="ClassHostFixture"/>, and to construct no factory of its own: a
/// <c>new</c> inside any of its methods is a per-test host wearing a class
/// fixture's name, and it is read out of the compiled IL rather than the
/// source so a helper method cannot hide one.</para>
/// </remarks>
public static class SharedHostPolicy
{
    /// <summary>
    /// The classes converted to one host per class, in the order they were
    /// converted. Each entry is a test class in this assembly.
    /// </summary>
    public static readonly IReadOnlyList<string> Converted = ["OgcEndpointTests"];

    /// <summary>Whether <paramref name="testClass"/> is on the register.</summary>
    public static bool IsConverted(Type testClass) =>
        Converted.Contains(testClass.Name, StringComparer.Ordinal);

    /// <summary>
    /// Why <paramref name="testClass"/> does not boot one host per class, or
    /// <see langword="null"/> when it does and the shape holds.
    /// </summary>
    public static string? WhyNotOneHostPerClass(Type testClass)
    {
        if (!IsConverted(testClass))
        {
            return null;
        }

        if (!DeclaresAClassFixture(testClass))
        {
            return $"{testClass.Name} is on {nameof(SharedHostPolicy)}.{nameof(Converted)} but does not "
                + $"implement IClassFixture<T>, so every test in it still boots its own host.";
        }

        var built = FactoriesConstructedIn(testClass).ToList();
        if (built.Count > 0)
        {
            return $"{testClass.Name} is on {nameof(SharedHostPolicy)}.{nameof(Converted)} but constructs "
                + $"{string.Join(", ", built)} in one of its own methods: a per-test host wearing a class "
                + "fixture's name. Take the host from the fixture.";
        }

        return null;
    }

    /// <summary>
    /// Whether the class takes an <c>IClassFixture&lt;T&gt;</c> from this
    /// suite's own hierarchy, rather than some other fixture entirely.
    /// </summary>
    private static bool DeclaresAClassFixture(Type testClass)
    {
        foreach (var contract in testClass.GetInterfaces())
        {
            if (contract.IsGenericType
                && contract.GetGenericTypeDefinition() == typeof(IClassFixture<>)
                && typeof(ClassHostFixture).IsAssignableFrom(contract.GetGenericArguments()[0]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The host factories the class constructs, read out of the compiled IL of
    /// its own methods and constructors — a local, a field initialiser or a
    /// private helper all count, because all three are a per-test host.
    /// </summary>
    private static IEnumerable<Type> FactoriesConstructedIn(Type testClass)
    {
        const BindingFlags Methods =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;

        foreach (var method in testClass.GetConstructors(BindingFlags.Instance | BindingFlags.Public
            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Cast<MethodBase>()
            .Concat(testClass.GetMethods(Methods)))
        {
            foreach (var token in ConstructedTokens(method))
            {
                var declaring = method.Module.ResolveMethod(token)?.DeclaringType;
                if (declaring is not null && typeof(WebApplicationFactory<Program>).IsAssignableFrom(declaring))
                {
                    yield return declaring;
                }
            }
        }
    }

    /// <summary>Every <c>newobj</c> method token in a method body.</summary>
    private static IEnumerable<int> ConstructedTokens(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null || il.Length == 0)
        {
            yield break;
        }

        for (var i = 0; i < il.Length;)
        {
            var code = (short)il[i++];
            if (code == 0xFE)
            {
                code = (short)(0xFE00 | il[i++]);
            }

            var opcode = OpCodesByValue.TryGetValue(code, out var found) ? found : default;
            var size = OperandSize(opcode.OperandType);
            if (code == 0x73)
            {
                yield return BitConverter.ToInt32(il, i);
            }

            i += size;
        }
    }

    private static int OperandSize(OperandType operand) => operand switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineMethod
            or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
            or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        _ => 0,
    };

    private static readonly Dictionary<short, OpCode> OpCodesByValue = ReadOpCodes();

    private static Dictionary<short, OpCode> ReadOpCodes()
    {
        var byValue = new Dictionary<short, OpCode>();
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is OpCode opcode)
            {
                byValue[opcode.Value] = opcode;
            }
        }

        return byValue;
    }
}
