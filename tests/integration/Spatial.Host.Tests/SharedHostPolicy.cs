using System.Reflection;
using System.Reflection.Emit;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

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
/// <para><see cref="Audited"/> is the ADR-0161 per-class audit behind that
/// register: every class that boots a host of its own carries a verdict —
/// convert, split or leave — and the evidence that decided it, so the classes
/// that were looked at and refused are as visible as the ones that
/// converted.</para>
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
    /// <summary>What the ADR-0161 per-class audit decided about a class.</summary>
    public enum HostVerdict
    {
        /// <summary>One host per class is safe here; the class is converted.</summary>
        Convert,

        /// <summary>The class carries more than one host's settings; split it by setting.</summary>
        Split,

        /// <summary>One host per class is not safe here; the per-test host stays.</summary>
        Leave,
    }

    /// <summary>What the audit decided about one class, and the evidence for it.</summary>
    /// <param name="Verdict">Convert, split or leave.</param>
    /// <param name="Evidence">Why — the setting, listing or shared state that decides it.</param>
    public readonly record struct Audit(HostVerdict Verdict, string Evidence);

    /// <summary>
    /// The classes converted to one host per class, in the order they were
    /// converted. Each entry is a test class in this assembly.
    /// </summary>
    public static readonly IReadOnlyList<string> Converted =
    [
        "OgcEndpointTests",
        "DiscoveryPageTests",
        "FeatureQueryPlanTests",
        "MapRenderTests",
        "MapTileTests",
        "ParityCensusTests",
        "PolarRenderTests",
        "StoreTransactionTests",
        "TileDataVersionTests",
    ];

    /// <summary>
    /// The ADR-0161 per-class audit: every class in this assembly that boots a
    /// host of its own is here with a verdict and the evidence for it, so the
    /// register of conversions cannot grow without the rest of the suite being
    /// judged too, and a class nobody has looked at is a failing gate rather
    /// than an omission nobody notices.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Audit> Audited = new Dictionary<string, Audit>(StringComparer.Ordinal)
    {
        ["AdminEndpointTests"] = new(
            HostVerdict.Leave,
            "two of the ADR-0160 blockers in one class: three tests inject Ingest:MaxBytes / "
            + "MaxFeatures / SkipMalformed per test, and /arcgis/admin/services is asserted on as a "
            + "listing of exactly the maps that test published, which a shared host would fill with "
            + "every other test's. public.parks is likewise ingested under one name by four tests."),
        ["AttachmentStoreWiringTests"] = new(
            HostVerdict.Leave,
            "already one host for the class (a factory in a field initialiser), and the test is the "
            + "DI composition the attachment store is registered into."),
        ["AuthEndpointTests"] = new(
            HostVerdict.Split,
            "four of the five tests share the class's configuration; An_expired_token_is_rejected "
            + "injects Spatial:Auth:TokenLifetime=00:00:00.0000001, so that one test moves to "
            + "AuthTokenExpiryTests and the rest convert."),
        ["CliEndToEndTests"] = new(
            HostVerdict.Leave,
            "each test writes its own project and upload files into the class directory and drives "
            + "the CLI against the host; three boots is not the cost, and the fixture's maps file "
            + "would be the CLI's publication store, which is a second shared path to own."),
        ["DiscoveryPageTests"] = new(
            HostVerdict.Convert,
            "settings are the class's (admin token, maps path) and no assertion reads a count: the "
            + "page is read for the map the test itself published."),
        ["EsriDocsEdgeCaseReplayTests"] = new(
            HostVerdict.Leave,
            "already one host for the class, and every replay republishes the one service name the "
            + "class shares — the shared-name republish ADR-0160 measured MapRegistry's max + 1 layer "
            + "ids against."),
        ["EsriDocsImageServerReplayTests"] = new(
            HostVerdict.Leave,
            "already one host for the class, with one published service name per replay; converting "
            + "would mean renaming the replayed service, which is the documentation fixture's "
            + "identity rather than the test's."),
        ["EsriDocsMapServerReplayTests"] = new(
            HostVerdict.Leave,
            "already one host for the class, on the PostGIS factory, republishing the documented "
            + "/api/maps/world service per replay."),
        ["EsriVectorTileRouteTests"] = new(
            HostVerdict.Leave,
            "already one host for the class; nothing to remove, and the vector tile cache is shared "
            + "with the rest of the suite's hosts anyway."),
        ["FeatureQueryPlanTests"] = new(
            HostVerdict.Convert,
            "every test already seeds public.plan_<guid>, so the per-test dataset identity the "
            + "shared host needs is already in place; no assertion reads a listing."),
        ["GeoServicesEditTests"] = new(
            HostVerdict.Leave,
            "EditableFactory(scanFallback: true) is injected by one test and not the others, and the "
            + "edit surface writes through to the memory store the class shares."),
        ["GeoServicesImageHonestyTests"] = new(
            HostVerdict.Leave,
            "the HonestyFactory is built per test from the raster fixture's own metadata (itemMetadata, "
            + "catalog flags), so the host's settings are the test's rather than the class's."),
        ["GeoServicesImageMetadataTests"] = new(
            HostVerdict.Leave,
            "nine MetadataFactory constructions differing in itemMetadata and catalog — the metadata "
            + "under test is injected configuration, not a per-test dataset."),
        ["GeoServicesImageMissingTests"] = new(
            HostVerdict.Leave,
            "eleven MissingFactory constructions differing in catalog / statistics / attributeTable, "
            + "which is exactly the per-test injected settings ADR-0160 names as the blocking shape."),
        ["GeoServicesAttachmentAuthTests"] = new(
            HostVerdict.Leave,
            "already one host for the class, built in the constructor."),
        ["GeoServicesAttachmentsTests"] = new(
            HostVerdict.Leave,
            "already one host for the class, built in the constructor."),
        ["GeoServicesFeatureOpsTests"] = new(
            HostVerdict.Leave,
            "already one host for the class, built in the constructor; the write tests share the "
            + "memory store the class writes to, which a per-test map name would not isolate."),
        ["GeoServicesImageReconTests"] = new(
            HostVerdict.Leave,
            "already one host for the class, built in the constructor."),
        ["GeoServicesImageTests"] = new(
            HostVerdict.Leave,
            "one host for the class and eleven more inside single tests, each an ImageFactory over a "
            + "different raster source, size or catalog flag — the injected settings are the "
            + "assertion, not the class's."),
        ["GeoServicesMapExportTests"] = new(
            HostVerdict.Leave,
            "already one host for the class, built in the constructor."),
        ["GeoServicesMapLegendTests"] = new(
            HostVerdict.Leave,
            "already one host for the class (a factory built in the constructor) and already "
            + "republishing one map name, so it is outside the ClassHostFixture shape but has no "
            + "per-test boot to remove."),
        ["GeoServicesMapOfflineTests"] = new(
            HostVerdict.Leave,
            "two hosts for the class, one of them a raster source registered per test; the offline "
            + "package route is asserted against a host whose raster wiring is the test's."),
        ["GeoServicesMapTests"] = new(
            HostVerdict.Leave,
            "already one host for the class, built in the constructor; the class is the largest "
            + "GeoServices surface and republishes one service name throughout."),
        ["GeoServicesRelationshipAuthTests"] = new(
            HostVerdict.Leave,
            "already one host for the class, built in the constructor."),
        ["GeoServicesRelationshipsTests"] = new(
            HostVerdict.Leave,
            "already one host for the class, built in the constructor."),
        ["GeoServicesSourceIdentityTests"] = new(
            HostVerdict.Leave,
            "already one host for the class, and its CountingStore is a singleton the tests read as a "
            + "shared counter, which is the shared-singleton shape a per-test dataset would not fix."),
        ["MapRenderTests"] = new(
            HostVerdict.Convert,
            "class settings only, and each test renders the map it published under its own name."),
        ["MapTileTests"] = new(
            HostVerdict.Convert,
            "class settings only; the cache-key test republishes under its own name, and the tile "
            + "cache key folds in the map name so no other test's render can answer for it."),
        ["OgcEndpointTests"] = new(
            HostVerdict.Convert,
            "ADR-0160's first conversion: 53 datasets on one host, every test publishing then reading "
            + "its own issued name back."),
        ["OgcApiTilesTests"] = new(
            HostVerdict.Leave,
            "already one host for the class, built in the constructor; the OGC tile routes read the "
            + "map registry the class publishes into."),
        ["ParityCensusTests"] = new(
            HostVerdict.Convert,
            "the census fixture is ingested under a per-test dataset name and published under a "
            + "per-test map name, so the shared memory store holds three datasets rather than one "
            + "re-ingested one."),
        ["PolarRenderTests"] = new(
            HostVerdict.Convert,
            "the polar GeoJSON is ingested under a per-test dataset name and published under a "
            + "per-test map name, which is the keyed-dataset identity ADR-0160 defers to this audit."),
        ["RemoteStoreWiringTests"] = new(
            HostVerdict.Leave,
            "the test is the DI composition itself: the ArcGIS REST adapter is registered by host "
            + "configuration, so the host's settings are the assertion."),
        ["ResumableUploadTests"] = new(
            HostVerdict.Leave,
            "one test injects Ingest:MaxBytes=64, and the upload staging area is a shared singleton: "
            + "the listing test asserts GET /api/uploads is empty after its own discard, which eleven "
            + "other tests' staged uploads would fill."),
        ["SeedEndpointTests"] = new(
            HostVerdict.Leave,
            "four different factory configurations (environment, a configuration dictionary, an auth "
            + "token), so the host's settings are per test across the class."),
        ["StoreTransactionTests"] = new(
            HostVerdict.Convert,
            "every dataset is public.txn_<guid> already, and the transaction lifecycle is id-keyed, "
            + "so nothing a test writes can be read by another."),
        ["TestHostClientTimeoutTests"] = new(
            HostVerdict.Leave,
            "the class is the gate on factory client timeouts and deliberately boots factories of "
            + "several shapes, including the PostGIS one; sharing a host would delete what it asserts."),
        ["TestParallelismTests"] = new(
            HostVerdict.Leave,
            "asserts on the shape of the run itself over a PostGIS host."),
        ["TileDataVersionTests"] = new(
            HostVerdict.Convert,
            "datasets are public.places_<guid> and the tile cache key folds in the dataset's content "
            + "version, so no other test's render can answer a first-request-miss assertion."),
        ["VectorTileEndpointTests"] = new(
            HostVerdict.Leave,
            "already one host for the class, built in the constructor."),
        ["WorkbenchHostingTests"] = new(
            HostVerdict.Leave,
            "two of the three tests inject Spatial:WebRoot — one with a real app, one with a missing "
            + "directory it asserts fails fast — so the host's settings are the test's."),
    };

    /// <summary>Whether <paramref name="testClass"/> is on the register.</summary>
    public static bool IsConverted(Type testClass) =>
        Converted.Contains(testClass.Name, StringComparer.Ordinal);

    /// <summary>
    /// The test classes in this assembly that boot a host of their own, read
    /// out of their compiled IL. This is the population the ADR-0161 audit
    /// has to account for: a class xUnit will run, that constructs a factory
    /// in one of its own methods. Compiler-generated closure types and the
    /// helpers a class nests beside itself are not test classes and are not in
    /// it.
    /// </summary>
    public static IReadOnlyList<Type> ClassesBootingTheirOwnHost() =>
        typeof(SharedHostPolicy).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract
                && !type.ContainsGenericParameters
                && IsATestClass(type)
                && FactoriesConstructedIn(type).Any())
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>Whether xUnit will run <paramref name="type"/>'s own methods.</summary>
    private static bool IsATestClass(Type type) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Any(method => method.GetCustomAttributes()
                .Any(attribute => attribute is FactAttribute or TheoryAttribute));

    /// <summary>The audit verdict for <paramref name="testClass"/>, if it has one.</summary>
    public static Audit? VerdictFor(Type testClass) =>
        Audited.TryGetValue(testClass.Name, out var audit) ? audit : null;

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
                // A token from a generic method cannot be resolved without an
                // instantiation context, and the read has no business failing
                // the run over a class nobody claimed: the method is skipped
                // rather than the audit.
                MethodBase? resolved;
                try
                {
                    resolved = method.Module.ResolveMethod(token);
                }
                catch (BadImageFormatException)
                {
                    continue;
                }

                var declaring = resolved?.DeclaringType;
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
        if (method.IsGenericMethod)
        {
            yield break;
        }

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
