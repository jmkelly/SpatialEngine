using System.Security.Cryptography;
using Spatial.Rendering.Skia.Drawing;

namespace Spatial.Rendering.Skia.Tests;

/// <summary>
/// The embedded font face registry: every bundled face is digest-pinned, and a
/// <c>text-font</c> request walks the documented fallback chain instead of
/// being rejected (ADR-0080).
/// </summary>
public sealed class FontFaceRegistryTests
{
    [Fact]
    public void EveryBundledFaceMatchesItsPinnedHash()
    {
        foreach (var face in FontFaceRegistry.Default.Faces)
        {
            var hash = Convert.ToHexString(SHA256.HashData(ManifestResources.Read(face.Resource)));

            Assert.Equal(face.Sha256, hash);
        }
    }

    [Fact]
    public void TheBundleCarriesTheRegularBoldAndItalicFacesOfOneFamily()
    {
        var faces = FontFaceRegistry.Default.Faces;

        Assert.Equal(["Noto Sans"], faces.Select(face => face.Family).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(
            [(400, 0), (400, 1), (700, 0), (700, 1)],
            faces.Select(face => (face.Weight, Style: (int)face.Style))
                .OrderBy(face => face.Weight)
                .ThenBy(face => face.Style));
    }

    [Fact]
    public void TheDefaultFaceIsTheRegularWeight()
    {
        var face = FontFaceRegistry.Default.DefaultFace;

        Assert.Equal("Noto Sans", face.Family);
        Assert.Equal(400, face.Weight);
        Assert.Equal(FontStyle.Normal, face.Style);
    }

    [Theory]
    [InlineData("Noto Sans", 400, 0)]
    [InlineData("Noto Sans Regular", 400, 0)]
    [InlineData("noto sans bold", 700, 0)]
    [InlineData("  Noto   Sans   Bold  ", 700, 0)]
    [InlineData("Noto Sans Italic", 400, 1)]
    [InlineData("Noto Sans Bold Italic", 700, 1)]
    public void Resolve_ReadsTheFamilyWeightAndStyle(string name, int weight, int style)
    {
        var face = FontFaceRegistry.Default.Resolve([name]);

        Assert.Equal(weight, face.Weight);
        Assert.Equal((FontStyle)style, face.Style);
    }

    /// <summary>
    /// A weight the bundle does not carry takes the nearest bundled weight, CSS
    /// font-matching order: heavier first, then lighter, style held.
    /// </summary>
    [Theory]
    [InlineData("Noto Sans SemiBold", 700, 0)]
    [InlineData("Noto Sans Light", 400, 0)]
    [InlineData("Noto Sans Medium", 700, 0)]
    [InlineData("Noto Sans Medium Italic", 700, 1)]
    public void Resolve_TakesTheNearestBundledWeight(string name, int weight, int style)
    {
        var face = FontFaceRegistry.Default.Resolve([name]);

        Assert.Equal(weight, face.Weight);
        Assert.Equal((FontStyle)style, face.Style);
    }

    [Fact]
    public void Resolve_TakesTheFirstNameInTheListTheBundleCarries()
    {
        var face = FontFaceRegistry.Default.Resolve(["Comic Sans MS", "Helvetica", "Noto Sans Bold"]);

        Assert.Equal(700, face.Weight);
    }

    [Theory]
    [InlineData("Comic Sans MS")]
    [InlineData("sans-serif")]
    [InlineData("Arial Unicode MS")]
    [InlineData("a family that is not real")]
    public void Resolve_AnUnknownFamilyFallsBackToTheDefaultFace(string name)
    {
        var face = FontFaceRegistry.Default.Resolve([name]);

        Assert.Equal(FontFaceRegistry.Default.DefaultFace, face);
    }

    [Fact]
    public void Resolve_AnEmptyRequestUsesTheDefaultFace()
    {
        Assert.Equal(FontFaceRegistry.Default.DefaultFace, FontFaceRegistry.Default.Resolve([]));
    }

    [Fact]
    public void TypefaceIsCachedAndCarriesTheBundledFamily()
    {
        var face = FontFaceRegistry.Default.Resolve(["Noto Sans Bold"]);

        var first = FontFaceRegistry.Default.Typeface(face);
        var second = FontFaceRegistry.Default.Typeface(face);

        Assert.Same(first, second);
        Assert.Equal("Noto Sans", first.FamilyName);
    }

    [Fact]
    public void AFaceOutsideTheBundleIsRejected()
    {
        var face = new FontFace("Elsewhere", 400, FontStyle.Normal, "Fonts.NotoSans-Regular.ttf", "0");

        Assert.Throws<InvalidOperationException>(() => FontFaceRegistry.Default.Typeface(face));
    }

    [Theory]
    [InlineData("Noto Sans 700", 700, 0)]
    [InlineData("Noto Sans Bold 700 Italic", 700, 1)]
    [InlineData("Noto Sans Oblique", 400, 1)]
    public void Resolve_ReadsNumericWeightsAndTheObliqueStyle(string name, int weight, int style)
    {
        var face = FontFaceRegistry.Default.Resolve([name]);

        Assert.Equal(weight, face.Weight);
        Assert.Equal((FontStyle)style, face.Style);
    }

    [Theory]
    [InlineData("Noto Sans Frobnicate")]
    [InlineData("Noto Sans 5000")]
    [InlineData("")]
    public void Resolve_AnUnknownStyleTokenFallsBackToTheDefaultFace(string name)
    {
        Assert.Equal(FontFaceRegistry.Default.DefaultFace, FontFaceRegistry.Default.Resolve([name]));
    }

    [Fact]
    public void Resolve_FallsBackToTheUprightFaceWhenNoSlantedFaceIsBundled()
    {
        var registry = new FontFaceRegistry([new FontFace("Solo", 400, FontStyle.Normal, "solo.ttf", "0")]);

        var face = registry.Resolve(["Solo Italic"]);

        Assert.Equal(FontStyle.Normal, face.Style);
        Assert.Equal("Solo", face.Family);
    }

    [Fact]
    public void ADisposedRegistryRefusesToHandOutTypefaces()
    {
        var registry = new FontFaceRegistry(FontFaceRegistry.Default.Faces);
        var face = registry.DefaultFace;

        Assert.Same(registry.Typeface(face), registry.Typeface(face));

        registry.Dispose();

        Assert.Throws<ObjectDisposedException>(() => registry.Typeface(face));
        registry.Dispose();
    }

    [Fact]
    public void ASessionReusesOneShaperPerFaceAndOnePerDistinctFace()
    {
        using var session = new FontSession();

        var first = session.Resolve(["Noto Sans Bold"]);
        var again = session.Resolve(["Noto Sans SemiBold"]);
        var italic = session.Resolve(["Noto Sans Italic"]);

        Assert.Same(first, again);
        Assert.NotSame(first, italic);
    }

    [Fact]
    public void ADisposedSessionRefusesToHandOutShapers()
    {
        var session = new FontSession();
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.Resolve([]));
    }

    [Fact]
    public void CreateFontSizesTheResolvedFace()
    {
        using var font = BundledFont.CreateFont(FontFaceRegistry.Default.Resolve(["Noto Sans Bold"]), 24);

        Assert.Equal(24, font.Size);
        Assert.Equal("Noto Sans", font.Typeface.FamilyName);
    }
}
