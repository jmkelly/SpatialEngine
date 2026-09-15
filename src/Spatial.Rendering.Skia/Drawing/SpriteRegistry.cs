using SkiaSharp;
using Spatial.Contracts;
using Svg.Skia;

namespace Spatial.Rendering.Skia.Drawing;

/// <summary>
/// The embedded SVG sprite set (ADR-0049): a name index over
/// <c>Resources/Sprites/*.svg</c> parsed once by <c>Svg.Skia</c> into
/// thread-safe <see cref="SKPicture"/>s. A style may only reference a bundled
/// name — never a URL — so an unknown name is a typed <c>invalid.arguments</c>.
/// </summary>
internal sealed class SpriteRegistry : IDisposable
{
    private const string SpritePrefix = "Resources.Sprites.";
    private const string DefaultIcon = "default-marker";
    private readonly Dictionary<string, Sprite> _sprites = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>The process-wide registry of the assembly's embedded sprites.</summary>
    public static SpriteRegistry Default { get; } = LoadEmbedded();

    /// <summary>The bundled icon name, always present in <see cref="Default"/>.</summary>
    public static string DefaultName => DefaultIcon;

    public IReadOnlyCollection<string> Names => _sprites.Keys;

    /// <summary>Resolves a bundled icon, or throws a typed error naming the unknown reference.</summary>
    public SKPicture Get(string name) =>
        _sprites.TryGetValue(name, out var sprite)
            ? sprite.Picture
            : throw SpatialException.BadArguments(
                $"Unsupported icon-image '{name}'; bundled sprites: {string.Join(", ", _sprites.Keys.Order(StringComparer.Ordinal))}.");

    public static SpriteRegistry LoadEmbedded()
    {
        var registry = new SpriteRegistry();
        foreach (var resource in ManifestResources.NamesWithPrefix(SpritePrefix).Order(StringComparer.Ordinal))
        {
            var start = resource.IndexOf(SpritePrefix, StringComparison.Ordinal) + SpritePrefix.Length;
            var name = resource[start..];
            if (name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }

            registry.Add(name, ManifestResources.Read(resource));
        }

        return registry;
    }

    /// <summary>Builds a registry from raw SVG sources; used by tests to inject icons.</summary>
    public static SpriteRegistry FromSources(IEnumerable<KeyValuePair<string, string>> sources)
    {
        var registry = new SpriteRegistry();
        foreach (var (name, svg) in sources)
        {
            registry.Add(name, System.Text.Encoding.UTF8.GetBytes(svg));
        }

        return registry;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var sprite in _sprites.Values)
        {
            sprite.Svg.Dispose();
        }

        _sprites.Clear();
        _disposed = true;
    }

    private void Add(string name, byte[] svg)
    {
        var svgDocument = new SKSvg();
        using var stream = new MemoryStream(svg);
        var picture = svgDocument.Load(stream)
            ?? throw new InvalidOperationException($"The embedded sprite '{name}' is not a valid SVG.");
        _sprites[name] = new Sprite(svgDocument, picture);
    }

    private sealed record Sprite(SKSvg Svg, SKPicture Picture);
}
