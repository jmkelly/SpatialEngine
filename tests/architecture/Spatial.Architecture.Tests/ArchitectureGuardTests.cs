namespace Spatial.Architecture.Tests;

/// <summary>
/// Dependency and structure guardrails for the spatial engine.
///
/// Each rule is a deliberate boundary from the architecture plan
/// (see /architecture/implementation-plan.md and /architecture/decisions).
/// Changing a rule requires changing the plan and the affected ADRs first.
/// </summary>
public sealed class ArchitectureGuardTests
{
    private static readonly Lazy<RepositoryInfo> Repository = new(() =>
        RepositoryScanner.Load(RepositoryScanner.FindRepositoryRoot()));

    /// <summary>
    /// Core is a platform project. ADR-0001: geometry belongs in the core;
    /// ADR-0002/0003: algorithms and stores are plugins. The core must not
    /// reference databases, renderers, spatial algorithm packages or any project.
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
    /// ADR-0006/ADR-0013: plugins are isolated workers. The runtime routes to
    /// capabilities through contracts; it never references a concrete plugin
    /// implementation (provider, operations or transformations).
    /// </summary>
    [Fact]
    public void Runtime_references_no_concrete_plugin()
    {
        var allowed = new[] { "Spatial.Core", "Spatial.PluginSdk" };
        var violations = PlatformProject("Spatial.Runtime").ProjectReferences
            .Where(r => !allowed.Contains(r))
            .Select(r => $"{PlatformProject("Spatial.Runtime").RelativePath} must reference only {string.Join(", ", allowed)}, but references {r}.")
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// Plugin implementation projects exist to be launched as workers, not to
    /// be linked into the host, runtime or contracts. No platform project may
    /// reference a plugin implementation project.
    /// </summary>
    [Fact]
    public void Platform_projects_do_not_reference_plugin_implementations()
    {
        var violations = PlatformProjects()
            .SelectMany(project => project.ProjectReferences
                .Where(IsPluginImplementation)
                .Select(r => $"{project.RelativePath} references plugin implementation {r}; plugins run out of process."))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// Principle 7 / ADR-0005: plugins depend on contracts, never on another
    /// plugin implementation. A plugin that links a sibling plugin would drag
    /// its third-party types across the boundary the worker model exists to
    /// keep clean.
    /// </summary>
    [Fact]
    public void Plugin_implementations_do_not_reference_each_other()
    {
        var violations = PluginImplementationProjects()
            .SelectMany(project => project.ProjectReferences
                .Where(IsPluginImplementation)
                .Select(r => $"{project.RelativePath} references plugin implementation {r}; plugins depend on contracts, never on each other (principle 7)."))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// ADR-0005: third-party spatial/data types stay inside their owning
    /// plugin. The generic package rule allows <c>Microsoft.*</c>/<c>System.*</c>,
    /// so the concrete ADR-0005 families are checked explicitly here.
    /// </summary>
    [Fact]
    public void Platform_projects_do_not_reference_third_party_spatial_packages()
    {
        var violations = PlatformProjects()
            .SelectMany(project => project.Packages
                .Where(p => IsForbiddenSpatialPackage(p.Id))
                .Select(p => $"{project.RelativePath} references {p.Id}; third-party spatial/data types stay inside their owning plugin (ADR-0005)."))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// ADR-0018/ADR-0019: the host is independently executable and wires only
    /// platform projects; desktop shells and workers remain external.
    /// </summary>
    [Fact]
    public void Host_references_only_platform_projects()
    {
        var allowed = new[] { "Spatial.Core", "Spatial.Runtime", "Spatial.PluginSdk", "Spatial.PluginHost.DotNet" };
        var host = PlatformProject("Spatial.Host");
        var violations = host.ProjectReferences
            .Where(r => !allowed.Contains(r))
            .Select(r => $"{host.RelativePath} must reference only {string.Join(", ", allowed)}, but references {r}.")
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// Third-party spatial types must never cross public boundaries (ADR-0005).
    /// Platform projects may only use framework packages plus an explicit
    /// per-project allowlist that is extended deliberately, with an ADR.
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
    /// ADR-0016/ADR-0017: Tauri is a desktop shell, not part of the web client.
    /// The browser workbench and generated SDK run without Tauri APIs.
    /// </summary>
    [Fact]
    public void Web_clients_do_not_depend_on_tauri()
    {
        var violations = Repository.Value.WebClients
            .SelectMany(client => client.DependencyIds
                .Where(id => id.StartsWith("@tauri-apps/", StringComparison.Ordinal))
                .Select(id => $"{client.RelativePath} depends on {id}; the web client must run without Tauri."))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// Every project under /src and /tests is in the solution, and every
    /// solution project lives under /src or /tests. Orphan and stray projects
    /// escape review otherwise.
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

    /// <summary>Plugin implementation projects only; the matching `*.Tests` projects are excluded.</summary>
    private static IEnumerable<ProjectInfo> PluginImplementationProjects() =>
        Repository.Value.Projects.Where(p => IsPluginImplementation(p.Name)
            && !p.Name.EndsWith(".Tests", StringComparison.Ordinal));

    private static ProjectInfo PlatformProject(string name) =>
        Repository.Value.Projects.Single(p => p.Name == name);

    private static readonly string[] PlatformProjectNames =
    [
        "Spatial.Core",
        "Spatial.Runtime",
        "Spatial.PluginSdk",
        "Spatial.PluginHost.DotNet",
        "Spatial.Host",
    ];

    /// <summary>Explicit, ADR-backed exceptions to the framework-only rule.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> AllowedPackages =
        new Dictionary<string, string[]>(StringComparer.Ordinal);

    private static bool IsAllowedPackage(string projectName, string id) =>
        id.StartsWith("Microsoft.", StringComparison.Ordinal)
        || id.StartsWith("System.", StringComparison.Ordinal)
        || AllowedPackages.GetValueOrDefault(projectName)?.Contains(id) == true;

    private static bool IsPluginImplementation(string projectName) =>
        projectName.StartsWith("Spatial.Provider.", StringComparison.Ordinal)
        || projectName.StartsWith("Spatial.Operations.", StringComparison.Ordinal)
        || projectName.StartsWith("Spatial.Transformations.", StringComparison.Ordinal);

    /// <summary>The third-party families ADR-0005 keeps out of platform projects.</summary>
    private static readonly string[] ForbiddenSpatialPackagePrefixes =
    [
        "NetTopologySuite",
        "Npgsql",
        "Microsoft.EntityFrameworkCore",
        "ProjNET",
        "@tauri-apps/",
    ];

    private static bool IsForbiddenSpatialPackage(string id) =>
        ForbiddenSpatialPackagePrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.Ordinal));

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
