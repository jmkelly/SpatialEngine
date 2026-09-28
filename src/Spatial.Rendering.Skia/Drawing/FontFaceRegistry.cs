using SkiaSharp;

namespace Spatial.Rendering.Skia.Drawing;

/// <summary>
/// The embedded font registry (ADR-0080): the Noto Sans faces the assembly
/// carries, each digest-pinned, and the documented chain that resolves a
/// <c>text-font</c> request onto one of them. Nothing here reads the host font
/// manager, so a render never depends on what the host has installed.
/// </summary>
/// <remarks>
/// The chain for each name in the <c>text-font</c> list, in order:
/// <list type="number">
///   <item>the name's family, if it is bundled, at the requested weight and style;</item>
///   <item>otherwise the nearest bundled weight of that family, heavier first then lighter
///         (CSS font matching), and the upright face when no slanted one is bundled;</item>
///   <item>otherwise the next name in the list;</item>
///   <item>and when no name matches, the registry's default face.</item>
/// </list>
/// A family the bundle does not carry is therefore never an error: it falls to
/// the default face rather than failing the style.
/// </remarks>
internal sealed class FontFaceRegistry
{
    private const string DefaultResource = "Fonts.NotoSans-Regular.ttf";

    /// <summary>
    /// The Noto Sans Regular 2.003 family, all four faces from the same pinned
    /// upstream tag. The digests are asserted per face, so a silent asset swap
    /// fails loudly.
    /// </summary>
    private static readonly FontFace[] Embedded =
    [
        new("Noto Sans", 400, FontStyle.Normal, DefaultResource, "DAC8E68FE43FCA59D522FA5F763322CFB4A919C28957656C58E7836D915307D0"),
        new("Noto Sans", 700, FontStyle.Normal, "Fonts.NotoSans-Bold.ttf", "D62FF7C27AE901AE9B5C3EF0CAE4A1F091E28A7CE62B9E0FED86A38185F84971"),
        new("Noto Sans", 400, FontStyle.Italic, "Fonts.NotoSans-Italic.ttf", "FD0142325F12C857B3443C2390F7F4A697B5C4017FBAD4CFD16218CF33C4DDBA"),
        new("Noto Sans", 700, FontStyle.Italic, "Fonts.NotoSans-BoldItalic.ttf", "4A44BC45D89669B612D00483F01233B43CAAEF695F1998702126154A37093180"),
    ];

    private static readonly Dictionary<string, int> WeightNames = new(StringComparer.Ordinal)
    {
        ["thin"] = 100,
        ["hairline"] = 100,
        ["extralight"] = 200,
        ["ultralight"] = 200,
        ["light"] = 300,
        ["regular"] = 400,
        ["normal"] = 400,
        ["book"] = 400,
        ["roman"] = 400,
        ["medium"] = 500,
        ["semibold"] = 600,
        ["demibold"] = 600,
        ["bold"] = 700,
        ["extrabold"] = 800,
        ["ultrabold"] = 800,
        ["black"] = 900,
        ["heavy"] = 900,
    };

    private readonly Dictionary<string, SKTypeface> _typefaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<FontFace>> _families;
    private readonly object _gate = new();
    private bool _disposed;

    public FontFaceRegistry(IEnumerable<FontFace> faces)
    {
        ArgumentNullException.ThrowIfNull(faces);
        Faces = [.. faces];
        _families = Faces
            .GroupBy(face => face.Family, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (List<FontFace>)[.. group], StringComparer.OrdinalIgnoreCase);
        DefaultFace = Faces.FirstOrDefault(face => face.Weight == FontFace.RegularWeight && face.Style == FontStyle.Normal)
            ?? throw new InvalidOperationException("The font registry needs a Regular upright default face.");
    }

    /// <summary>The process-wide registry of the assembly's embedded faces.</summary>
    public static FontFaceRegistry Default { get; } = new(Embedded);

    /// <summary>Every bundled face, in registry order.</summary>
    public IReadOnlyList<FontFace> Faces { get; }

    /// <summary>The face a request that names nothing resolves to.</summary>
    public FontFace DefaultFace { get; }

    /// <summary>
    /// Resolves a <c>text-font</c> list to the first face the chain reaches, or
    /// the default face when no name matches. It never throws for an unknown
    /// family; only a malformed request (null) is the caller's error.
    /// </summary>
    public FontFace Resolve(IReadOnlyList<string> requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        foreach (var name in requested)
        {
            if (Match(name) is { } face)
            {
                return face;
            }
        }

        return DefaultFace;
    }

    /// <summary>
    /// The typeface for a bundled face, created once from the embedded bytes
    /// and cached, with the bytes digest-checked against the face's pin. A
    /// face that is not part of this registry is refused rather than loaded.
    /// </summary>
    public SKTypeface Typeface(FontFace face)
    {
        ArgumentNullException.ThrowIfNull(face);
        if (!Faces.Contains(face))
        {
            throw new InvalidOperationException($"The font face '{face.Name}' is not part of the embedded registry.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_typefaces.TryGetValue(face.Resource, out var cached))
            {
                return cached;
            }

            var bytes = ManifestResources.Read(face.Resource);
            var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            if (!string.Equals(actual, face.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The bundled font '{face.Resource}' has hash {actual}, not the pinned {face.Sha256}.");
            }

            // Keep the SKData alive beside the typeface: Skia reads the font
            // tables lazily for the lifetime of the face.
            var data = SKData.CreateCopy(bytes);
            _typefaces[face.Resource] = SKTypeface.FromData(data);
            return _typefaces[face.Resource];
        }
    }

    private FontFace? Match(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var normalized = Normalize(name);
        // Longest family first, so "Arial Unicode MS" wins over "Arial".
        foreach (var family in _families.Keys.OrderByDescending(key => key.Length))
        {
            if (!StartsWithFamily(normalized, family))
            {
                continue;
            }

            var rest = normalized[family.Length..].Trim();
            return rest.Length == 0
                ? Nearest(family, FontFace.RegularWeight, FontStyle.Normal)
                : TryParseStyle(rest, out var weight, out var style) ? Nearest(family, weight, style) : null;
        }

        return null;
    }

    private static bool StartsWithFamily(string normalized, string family) =>
        normalized.StartsWith(family, StringComparison.OrdinalIgnoreCase)
        && (normalized.Length == family.Length || normalized[family.Length] == ' ');

    /// <summary>Parses the weight and style tokens after a family name; an unknown token is not a font name.</summary>
    private static bool TryParseStyle(string rest, out int weight, out FontStyle style)
    {
        weight = FontFace.RegularWeight;
        style = FontStyle.Normal;
        foreach (var token in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (WeightNames.TryGetValue(token, out var named))
            {
                weight = named;
                continue;
            }

            if (token is "italic" or "oblique")
            {
                style = FontStyle.Italic;
                continue;
            }

            if (int.TryParse(token, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var numeric)
                && numeric is >= 1 and <= 1000)
            {
                weight = (int)Math.Round(numeric / 100.0) * 100;
                continue;
            }

            weight = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// CSS font matching over the bundled faces: the exact face, else the
    /// nearest weight heavier first and then lighter, else the family's
    /// upright faces, else anything in the family.
    /// </summary>
    private FontFace? Nearest(string family, int weight, FontStyle style)
    {
        var faces = _families[family];
        var pool = faces.Where(face => face.Style == style).ToArray();
        if (pool.Length == 0)
        {
            pool = [.. faces];
        }

        return pool.FirstOrDefault(face => face.Weight == weight)
            ?? pool.Where(face => face.Weight >= weight).OrderBy(face => face.Weight).FirstOrDefault()
            ?? pool.Where(face => face.Weight < weight).OrderByDescending(face => face.Weight).FirstOrDefault();
    }

    private static string Normalize(string name) =>
        string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var typeface in _typefaces.Values)
            {
                typeface.Dispose();
            }

            _typefaces.Clear();
            _disposed = true;
        }
    }
}
