namespace Spatial.Rendering.Skia.Drawing;

/// <summary>
/// Reads the assembly's embedded rendering assets (fonts, SVG sprites) by a
/// unique logical-name suffix. The assets are compiled into this assembly
/// (ADR-0049); nothing is read from the host filesystem or network.
/// </summary>
internal static class ManifestResources
{
    public static Stream Open(string suffix)
    {
        var assembly = typeof(ManifestResources).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(candidate => candidate.EndsWith(suffix, StringComparison.Ordinal));
        return name is null
            ? throw new InvalidOperationException($"Embedded resource ending in '{suffix}' was not found.")
            : assembly.GetManifestResourceStream(name)!;
    }

    public static byte[] Read(string suffix)
    {
        using var stream = Open(suffix);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public static IEnumerable<string> NamesWithPrefix(string prefix) =>
        typeof(ManifestResources).Assembly.GetManifestResourceNames()
            .Where(name => name.Contains(prefix, StringComparison.Ordinal));
}
