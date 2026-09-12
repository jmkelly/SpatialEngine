namespace Spatial.Architecture.Tests;

/// <summary>
/// Dependency and structure guardrails for the spatial engine (ADR-0033).
///
/// The engine is composed by DI: <c>Spatial.Core</c> holds values,
/// <c>Spatial.PluginSdk</c> holds service interfaces, implementation
/// projects own the verbs, and <c>Spatial.Host</c> wires them. Changing a
/// rule requires updating ADR-0033 first.
/// </summary>
public sealed class ArchitectureGuardTests
{
    private static readonly Lazy<RepositoryInfo> Repository = new(() =>
        RepositoryScanner.Load(RepositoryScanner.FindRepositoryRoot()));

    /// <summary>
    /// Core is values only: no packages, no project references (ADR-0001/0032).
    /// </summary>
    [Fact]
    public void Core_has_no_dependencies()
    {
        var core = PlatformProject("Spatial.Core");
        var violations = new List<string>();
        if (core.Packages.Count != 0 || core.ProjectReferences.Count != 0)
        {
            violations.Add(
                $"{core.RelativePath} must have no packages and no project references, " +
                $"but declares {Describe(core.Packages.Select(x => x.Id), core.ProjectReferences)}.");
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// ADR-0033: the SDK holds interfaces over Core only; it takes no
    /// third-party packages and references nothing but Core.
    /// </summary>
    [Fact]
    public void PluginSdk_references_only_core()
    {
        var sdk = PlatformProject("Spatial.PluginSdk");
        var violations = new List<string>();
        violations.AddRange(sdk.ProjectReferences
            .Where(r => r != "Spatial.Core")
            .Select(r => $"{sdk.RelativePath} must reference only Spatial.Core, but references {r}."));
        violations.AddRange(sdk.Packages
            .Select(p => $"{sdk.RelativePath} must take no packages, but references {p.Id}."));

        Assert.Empty(violations);
    }

    /// <summary>
    /// ADR-0033/ADR-0005: implementations link Core + SDK only; third-party
    /// spatial packages stay inside the owning implementation. ADR-0035 adds
    /// the shared Esri codec as a permitted reference for the two boundary
    /// projects (they share only <c>Spatial.Interop.Esri</c>, never each
    /// other).
    /// </summary>
    [Fact]
    public void Implementation_projects_reference_only_core_and_sdk()
    {
        var allowed = new[] { "Spatial.Core", "Spatial.PluginSdk", "Spatial.Interop.Esri" };
        var violations = ImplementationProjects()
            .SelectMany(project => project.ProjectReferences
                .Where(r => !allowed.Contains(r) && !AllowedBoundaryReferences(project.Name).Contains(r))
                .Select(r => $"{project.RelativePath} must reference only {string.Join(", ", allowed)}, but references {r}."))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// ADR-0035: the shared Esri wire codec references Core only — no NTS,
    /// no ASP.NET, no HttpClient.
    /// </summary>
    [Fact]
    public void Interop_projects_reference_only_core()
    {
        var violations = Repository.Value.Projects
            .Where(project => project.Name.StartsWith("Spatial.Interop.", StringComparison.Ordinal)
                && project.RelativePath.Replace('\\', '/').StartsWith("src/", StringComparison.Ordinal))
            .SelectMany(project => project.ProjectReferences
                .Where(reference => reference != "Spatial.Core")
                .Select(reference => $"{project.RelativePath} must reference only Spatial.Core, but references {reference}."))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// ADR-0033: the host composes everything by DI — Core, SDK and the
    /// implementation projects — and nothing else.
    /// </summary>
    [Fact]
    public void Host_references_only_expected_projects()
    {
        var allowed = new[]
        {
            "Spatial.Core",
            "Spatial.PluginSdk",
            "Spatial.Interop.Ingest",
            "Spatial.Operations.NetTopologySuite",
            "Spatial.Transformations.ProjNet",
            "Spatial.Provider.Demo",
            "Spatial.Provider.Memory",
            "Spatial.Provider.PostGIS",
            "Spatial.Provider.Publications",
            "Spatial.Adapter.GeoServices",
            "Spatial.Provider.ArcGisRest",
            "Spatial.Rendering.Skia",
            "Spatial.Imagery.Vips",
        };
        var host = PlatformProject("Spatial.Host");
        var violations = host.ProjectReferences
            .Where(r => !allowed.Contains(r))
            .Select(r => $"{host.RelativePath} must reference only {string.Join(", ", allowed)}, but references {r}.")
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// ADR-0034: the Aspire AppHost is a development composition root — it
    /// orchestrates the host and infrastructure and links no engine project
    /// directly (no spatial logic, no contracts).
    /// </summary>
    [Fact]
    public void AppHost_references_only_the_host()
    {
        var allowed = new[] { "Spatial.Host" };
        var appHost = PlatformProject("Spatial.AppHost");
        var violations = appHost.ProjectReferences
            .Where(r => !allowed.Contains(r))
            .Select(r => $"{appHost.RelativePath} must reference only {string.Join(", ", allowed)}, but references {r}.")
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// Third-party spatial types must never leak into shared contracts
    /// (ADR-0005). Platform projects may only use framework packages plus an
    /// explicit per-project allowlist that is extended deliberately, with an ADR.
    /// </summary>
    [Fact]
    public void Platform_projects_use_only_allowlisted_packages()
    {
        var violations = PlatformProjects()
            .SelectMany(project => project.Packages
                .Where(p => !IsAllowedPackage(project.Name, p.Id))
                .Select(p =>
                    $"{project.RelativePath} references package {p.Id}, which is not on the platform allowlist. " +
                    "Add the package with an architecture decision, never silently."))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// All package versions live in Directory.Packages.props (central package
    /// management). Inline versions fragment the dependency set.
    /// </summary>
    [Fact]
    public void No_inline_package_versions()
    {
        var violations = Repository.Value.Projects
            .SelectMany(project => project.Packages
                .Where(p => p.Version is not null)
                .Select(p =>
                    $"{project.RelativePath} pins {p.Id} to {p.Version}; versions belong in Directory.Packages.props."))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// Every project under /src and /tests is in the solution, and every
    /// solution project lives under /src or /tests.
    /// </summary>
    [Fact]
    public void Solution_membership_is_exact()
    {
        var solutionItems = Repository.Value.SolutionProjects
            .Select(Normalise)
            .ToHashSet(StringComparer.Ordinal);

        var orphans = Repository.Value.Projects
            .Where(p => !solutionItems.Contains(Normalise(p.RelativePath)))
            .Select(p => $"{p.RelativePath} is not in the solution.")
            .ToList();

        var strays = Repository.Value.SolutionProjects
            .Where(path => !ProjectPath(path).StartsWith("src" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !ProjectPath(path).StartsWith("tests" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(path => $"{path} is in the solution but lives outside /src or /tests.")
            .ToList();

        Assert.Empty(orphans.Concat(strays));
    }

    private static IEnumerable<ProjectInfo> PlatformProjects() =>
        PlatformProjectNames.Select(name => Repository.Value.Projects.Single(p => p.Name == name));

    private static IEnumerable<ProjectInfo> ImplementationProjects() =>
        ImplementationProjectNames.Select(name => Repository.Value.Projects.Single(p => p.Name == name));

    private static ProjectInfo PlatformProject(string name) =>
        Repository.Value.Projects.Single(p => p.Name == name);

    private static readonly string[] PlatformProjectNames =
    [
        "Spatial.AppHost",
        "Spatial.Core",
        "Spatial.PluginSdk",
        "Spatial.Interop.Esri",
        "Spatial.Interop.Ingest",
        "Spatial.Operations.NetTopologySuite",
        "Spatial.Transformations.ProjNet",
        "Spatial.Provider.Demo",
        "Spatial.Provider.Memory",
        "Spatial.Provider.PostGIS",
        "Spatial.Provider.Publications",
        "Spatial.Adapter.GeoServices",
        "Spatial.Provider.ArcGisRest",
        "Spatial.Rendering.Skia",
        "Spatial.Imagery.Vips",
        "Spatial.Host",
    ];

    private static readonly string[] ImplementationProjectNames =
    [
        "Spatial.Operations.NetTopologySuite",
        "Spatial.Transformations.ProjNet",
        "Spatial.Provider.Demo",
        "Spatial.Provider.Memory",
        "Spatial.Provider.PostGIS",
        "Spatial.Provider.Publications",
        "Spatial.Adapter.GeoServices",
        "Spatial.Provider.ArcGisRest",
        "Spatial.Rendering.Skia",
        "Spatial.Imagery.Vips",
    ];

    /// <summary>ADR-0035 boundary projects may also reference the shared Esri codec.</summary>
    private static string[] AllowedBoundaryReferences(string projectName) => projectName switch
    {
        "Spatial.Adapter.GeoServices" => ["Spatial.Interop.Esri", "Spatial.Interop.Ingest"],
        "Spatial.Provider.ArcGisRest" => ["Spatial.Interop.Esri"],
        _ => [],
    };

    /// <summary>Explicit, ADR-backed exceptions to the framework-only rule.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> AllowedPackages =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Spatial.AppHost"] =
            [
                "Aspire.Hosting.AppHost",
                "Aspire.Hosting.JavaScript",
                "Aspire.Hosting.PostgreSQL",
                "Aspire.Hosting.Seq",
            ],
            ["Spatial.Host"] = ["Microsoft.AspNetCore.OpenApi", "Serilog.AspNetCore", "Serilog.Sinks.Seq"],
            ["Spatial.Operations.NetTopologySuite"] = ["NetTopologySuite"],
            ["Spatial.Transformations.ProjNet"] = ["ProjNET"],
            ["Spatial.Provider.PostGIS"] = ["Npgsql"],
            ["Spatial.Rendering.Skia"] = ["SkiaSharp", "SkiaSharp.NativeAssets.Linux.NoDependencies"],
            ["Spatial.Imagery.Vips"] = ["NetVips", "NetVips.Native"],
        };

    private static bool IsAllowedPackage(string projectName, string id) =>
        id.StartsWith("Microsoft.", StringComparison.Ordinal)
        || id.StartsWith("System.", StringComparison.Ordinal)
        || AllowedPackages.GetValueOrDefault(projectName)?.Contains(id) == true;

    private static string Normalise(string relativePath) =>
        relativePath.Replace('/', Path.DirectorySeparatorChar);

    private static string ProjectPath(string solutionEntry) =>
        Normalise(solutionEntry);

    private static string Describe(IEnumerable<string> packages, IEnumerable<string> references)
    {
        var parts = new List<string>();
        if (packages.Any())
        {
            parts.Add($"packages [{string.Join(", ", packages)}]");
        }

        if (references.Any())
        {
            parts.Add($"references [{string.Join(", ", references)}]");
        }

        return string.Join(" and ", parts);
    }
}
