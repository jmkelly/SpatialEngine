using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Drawing;

/// <summary>One run of a shaped line: the text it covers and where it sits.</summary>
internal readonly record struct ShapedRun(int Start, int Length, float X)
{
    public string Slice(string text) => text.Substring(Start, Length);
}

/// <summary>
/// One shaped line of a label, cut into the runs that are drawn separately.
/// With no letter spacing a line is a single run drawn as one shaped string —
/// the path the committed goldens were blessed on. With letter spacing the
/// HarfBuzz cluster map splits the line into runs and the tracking is the gap
/// between them, so shaping stays HarfBuzz's for both paths.
/// </summary>
internal sealed record ShapedLine(string Text, IReadOnlyList<ShapedRun> Runs, float Width)
{
    public bool Tracked => Runs.Count > 1;

    public bool IsEmpty => Runs.Count == 0;
}

/// <summary>
/// A label shaped once and measured for every candidate it might take
/// (ADR-0075). The text transforms are an evaluation step on the shaped
/// string, not on the style: <c>text-transform</c> before shaping,
/// <c>text-letter-spacing</c> as extra advance between the shaped runs, and
/// <c>text-line-height</c> as the advance between the lines of a multi-line
/// field.
/// </summary>
internal sealed class ShapedLabel
{
    private ShapedLabel(IReadOnlyList<ShapedLine> lines, float width, float glyphHeight, float lineAdvance)
    {
        Lines = lines;
        Width = width;
        GlyphHeight = glyphHeight;
        LineAdvance = lineAdvance;
    }

    /// <summary>The shaped lines, top to bottom.</summary>
    public IReadOnlyList<ShapedLine> Lines { get; }

    /// <summary>The widest line's advance width.</summary>
    public float Width { get; }

    /// <summary>The height of one line's glyph box, from the font metrics.</summary>
    public float GlyphHeight { get; }

    /// <summary>The baseline-to-baseline distance between lines.</summary>
    public float LineAdvance { get; }

    /// <summary>The full label box: the glyph box plus the extra lines below it.</summary>
    public float Height => GlyphHeight + ((Lines.Count - 1) * LineAdvance);

    /// <summary>
    /// Shapes a label: applies the text transform, splits the field on
    /// newlines, shapes each line and applies the letter spacing between the
    /// runs the shaper's cluster map produced. Null when nothing was shaped.
    /// </summary>
    public static ShapedLabel? Shape(
        string text, SKFont font, SKFontMetrics metrics, SKShaper shaper, SymbolOptions options)
    {
        var transformed = Transform(text, options.TextTransform);
        var tracking = (float)(options.LetterSpacing * options.Size);
        var lines = new List<ShapedLine>();
        var width = 0f;

        foreach (var line in transformed.Split('\n'))
        {
            var shaped = ShapeLine(line, font, shaper, tracking);
            lines.Add(shaped);
            width = Math.Max(width, shaped.Width);
        }

        var label = new ShapedLabel(lines, width, metrics.Descent - metrics.Ascent, (float)(options.LineHeight * options.Size));
        return label.Width <= 0 ? null : label;
    }

    /// <summary>The text transform, applied to the field before it is shaped.</summary>
    public static string Transform(string text, SymbolTextTransform transform) => transform switch
    {
        SymbolTextTransform.Uppercase => text.ToUpperInvariant(),
        SymbolTextTransform.Lowercase => text.ToLowerInvariant(),
        _ => text,
    };

    private static ShapedLine ShapeLine(string text, SKFont font, SKShaper shaper, float tracking)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new ShapedLine(text, [], 0);
        }

        var result = shaper.Shape(text, font);
        if (result.Codepoints.Length == 0)
        {
            return new ShapedLine(text, [], 0);
        }

        if (tracking == 0)
        {
            return new ShapedLine(text, [new ShapedRun(0, text.Length, 0)], result.Width);
        }

        var runs = Runs(text, result, tracking);

        // The runs tile the shaped line, so the tracked width is the shaped
        // width plus one gap per run boundary.
        var width = runs.Count > 1 ? result.Width + (tracking * (runs.Count - 1)) : result.Width;
        return new ShapedLine(text, runs, width);
    }

    /// <summary>
    /// Cuts a shaped line where the cluster map moves to a new character, and
    /// adds the tracking to each run's offset. A run's width is the distance
    /// to the next run's start, and the last run runs to the shaped width.
    /// </summary>
    private static List<ShapedRun> Runs(string text, SKShaper.Result result, float tracking)
    {
        var runs = new List<ShapedRun>();
        var start = 0;
        var origin = 0f;
        for (var index = 1; index < result.Clusters.Length; index++)
        {
            if (result.Clusters[index] == result.Clusters[index - 1])
            {
                continue;
            }

            runs.Add(new ShapedRun(start, (int)result.Clusters[index] - start, origin));
            origin = result.Points[index].X + (tracking * runs.Count);
            start = (int)result.Clusters[index];
        }

        if (start < text.Length)
        {
            runs.Add(new ShapedRun(start, text.Length - start, origin));
        }

        return runs;
    }
}
