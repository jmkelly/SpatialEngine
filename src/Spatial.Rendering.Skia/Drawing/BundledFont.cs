using System.Security.Cryptography;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Spatial.Rendering.Skia.Drawing;

/// <summary>
/// The single bundled text face (ADR-0049): created from an embedded
/// assembly resource, never from the host font manager, so shaping produces
/// identical glyphs on every machine. The resource bytes are hashed against
/// the pinned digest so a silent asset swap fails loudly.
/// </summary>
internal sealed class BundledFont : IDisposable
{
    /// <summary>The bundled family name (Noto Sans Regular 2.003, OFL-1.1).</summary>
    public const string FamilyName = "Noto Sans";

    /// <summary>The pinned SHA-256 of <c>Resources/Fonts/NotoSans-Regular.ttf</c>.</summary>
    public const string ExpectedSha256 = "DAC8E68FE43FCA59D522FA5F763322CFB4A919C28957656C58E7836D915307D0";

    private const string ResourceSuffix = "Fonts.NotoSans-Regular.ttf";

    private static readonly Lazy<FaceData> Face = new(CreateFace, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly SKShaper _shaper = new(Face.Value.Typeface);
    private bool _disposed;

    /// <summary>A shaper over the bundled face; one per render (HarfBuzz shaping is not concurrent).</summary>
    public SKShaper Shaper => _shaper;

    /// <summary>Creates a shaped font at <paramref name="size"/> pixels over the bundled face.</summary>
    public static SKFont CreateFont(double size) => new(Face.Value.Typeface, (float)size)
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

    private static FaceData CreateFace()
    {
        var bytes = ManifestResources.Read(ResourceSuffix);
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actual, ExpectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The bundled font '{ResourceSuffix}' has hash {actual}, not the pinned {ExpectedSha256}.");
        }

        // Keep the SKData alive beside the typeface: Skia reads the font table
        // lazily for the lifetime of the face.
        var data = SKData.CreateCopy(bytes);
        return new FaceData(SKTypeface.FromData(data), data);
    }

    private sealed record FaceData(SKTypeface Typeface, SKData Data);
}
