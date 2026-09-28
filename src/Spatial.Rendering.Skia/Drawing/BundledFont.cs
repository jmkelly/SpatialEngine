using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Spatial.Rendering.Skia.Drawing;

/// <summary>
/// A HarfBuzz shaper over one bundled face (ADR-0049): the typeface comes from
/// <see cref="FontFaceRegistry"/>, never from the host font manager, so shaping
/// produces identical glyphs on every machine. HarfBuzz shaping is not
/// concurrent, so a shaper is owned by one render and not shared.
/// </summary>
internal sealed class BundledFont : IDisposable
{
    private readonly SKShaper _shaper;
    private bool _disposed;

    /// <summary>Creates a shaper over a bundled face.</summary>
    public BundledFont(FontFace face, FontFaceRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(face);
        Face = face;
        _shaper = new SKShaper((registry ?? FontFaceRegistry.Default).Typeface(face));
    }

    /// <summary>The bundled face this shaper shapes with.</summary>
    public FontFace Face { get; }

    /// <summary>A shaper over the bundled face; one per render (HarfBuzz shaping is not concurrent).</summary>
    public SKShaper Shaper => _shaper;

    /// <summary>Creates a shaped font at <paramref name="size"/> pixels over a bundled face.</summary>
    public static SKFont CreateFont(FontFace face, double size, FontFaceRegistry? registry = null) =>
        new((registry ?? FontFaceRegistry.Default).Typeface(face), (float)size)
        {
            Subpixel = false,
            Hinting = SKFontHinting.Normal,
            Edging = SKFontEdging.Antialias,
        };

    public void Dispose()
    {
        if (!_disposed)
        {
            _shaper.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// The shapers one render uses, created lazily per resolved face and disposed
/// with the render. A layer resolves its <c>text-font</c> request through the
/// registry's fallback chain, so two layers asking for the same face share one
/// shaper and two layers asking for different faces get their own.
/// </summary>
internal sealed class FontSession : IDisposable
{
    private readonly Dictionary<FontFace, BundledFont> _fonts = [];
    private bool _disposed;

    /// <summary>Resolves a <c>text-font</c> list to a shaper for the face it reaches.</summary>
    public BundledFont Resolve(IReadOnlyList<string> requested)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var face = FontFaceRegistry.Default.Resolve(requested);
        if (!_fonts.TryGetValue(face, out var font))
        {
            _fonts[face] = font = new BundledFont(face);
        }

        return font;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var font in _fonts.Values)
        {
            font.Dispose();
        }

        _fonts.Clear();
        _disposed = true;
    }
}
