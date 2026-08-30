using System.Text.Json;
using System.Xml.Linq;

namespace Spatial.Architecture.Tests;

/// <summary>
/// Lightweight, dependency-free model of the repository's declared structure.
/// The guard inspects project and manifest files, not compiled output, so it
/// works from a clean checkout without a build and catches violations even
/// when referencing assemblies cannot be loaded.
/// </summary>
public sealed record PackageReference(string Id, string? Version);

public sealed record ProjectInfo(
    string Name,
    string RelativePath,
    IReadOnlyList<PackageReference> Packages,
    IReadOnlyList<string> ProjectReferences);

public sealed record WebClientInfo(string RelativePath, IReadOnlyList<string> DependencyIds);

public sealed record RepositoryInfo(
    string Root,
    IReadOnlyList<ProjectInfo> Projects,
    IReadOnlyList<WebClientInfo> WebClients,
    IReadOnlyList<string> SolutionProjects);

public static class RepositoryScanner
{
    public static RepositoryInfo Load(string root)
    {
        var projects = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(LoadProject)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        var webClients = new List<WebClientInfo>();
        foreach (var area in new[] { "apps", "clients" })
        {
            var dir = Path.Combine(root, area);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var manifest in Directory.EnumerateFiles(dir, "package.json", SearchOption.AllDirectories))
            {
                if (manifest.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                webClients.Add(LoadWebClient(manifest, root));
            }
        }

        var solutionProjects = LoadSolutionProjects(root);

        return new RepositoryInfo(root, projects, webClients, solutionProjects);
    }

    /// <summary>Locates the repository root by walking up from the test output directory.</summary>
    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root not found: no Directory.Build.props found above the test output directory.");
    }

    private static ProjectInfo LoadProject(string path)
    {
        var document = XDocument.Load(path, LoadOptions.SetLineInfo);
        var packages = new List<PackageReference>();
        var references = new List<string>();

        foreach (var element in document.Descendants().Where(e => e.Name.LocalName is "PackageReference" or "ProjectReference"))
        {
            var id = element.Attribute("Include")?.Value;
            if (id is null)
            {
                continue;
            }

            if (element.Name.LocalName == "PackageReference")
            {
                packages.Add(new PackageReference(id, element.Attribute("Version")?.Value));
            }
            else
            {
                // ProjectReference paths use backslashes even on Linux; normalise
                // before treating them as file-system paths.
                references.Add(Path.GetFileNameWithoutExtension(id.Replace('\\', Path.DirectorySeparatorChar)));
            }
        }

        return new ProjectInfo(
            Path.GetFileNameWithoutExtension(path),
            Path.GetRelativePath(FindRepositoryRoot(), path),
            packages,
            references);
    }

    private static WebClientInfo LoadWebClient(string path, string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var dependencies = new List<string>();
        foreach (var section in new[] { "dependencies", "devDependencies" })
        {
            if (document.RootElement.TryGetProperty(section, out var property))
            {
                dependencies.AddRange(property.EnumerateObject().Select(p => p.Name));
            }
        }

        return new WebClientInfo(Path.GetRelativePath(root, path), dependencies);
    }

    private static List<string> LoadSolutionProjects(string root)
    {
        var solution = Directory.EnumerateFiles(root, "SpatialEngine.sln*", SearchOption.TopDirectoryOnly).FirstOrDefault()
            ?? throw new InvalidOperationException("Solution file not found at repository root.");

        var projects = new List<string>();
        if (solution.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            var document = XDocument.Load(solution);
            foreach (var project in document.Descendants().Where(e => e.Name.LocalName == "Project"))
            {
                if (project.Attribute("Path")?.Value is { } path)
                {
                    projects.Add(path);
                }
            }
        }
        else
        {
            foreach (var line in File.ReadAllLines(solution))
            {
                if (line.TrimStart().StartsWith("Project(\"", StringComparison.Ordinal))
                {
                    projects.Add(line.Split('"')[3]);
                }
            }
        }

        return projects;
    }
}
