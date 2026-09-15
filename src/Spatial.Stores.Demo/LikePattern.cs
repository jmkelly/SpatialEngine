namespace Spatial.Stores.Demo;

/// <summary>
/// LIKE-style pattern matching over dataset ids: '%' matches any run of
/// characters, '_' matches exactly one character (the demo catalogue's
/// filter contract, mirrored from the PostGIS runner's shape). The matcher
/// is a pure function on strings — no catalog or invocation state — kept
/// apart from <see cref="DemoRunner"/> so the dispatch surface stays slim.
/// </summary>
internal static class LikePattern
{
    /// <summary>
    /// Whether <paramref name="text"/> matches <paramref name="pattern"/>.
    /// '%'-splitting turns the naive recursive backtracker into a linear
    /// walk: the wildcard-free head and tail anchor the text, and every
    /// segment between two '%' anchors at its first possible position
    /// (shifting a segment later can only tighten the remaining bounds, so
    /// greedy placement never loses a match).
    /// </summary>
    public static bool Matches(string text, string? pattern)
    {
        if (pattern is null)
        {
            return true;
        }

        var segments = pattern.Split('%');
        if (segments.Length == 1)
        {
            return text.Length == pattern.Length && SegmentMatches(text, 0, pattern);
        }

        return MatchCore(text, segments);
    }

    /// <summary>Walks the segments between the bounding wildcards against the text.</summary>
    private static bool MatchCore(string text, string[] segments)
    {
        var cursor = MatchHead(text, segments[0]);
        if (cursor < 0)
        {
            return false;
        }

        for (var i = 1; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0)
            {
                continue;
            }

            var at = FindSegment(text, cursor, segment);
            if (at < 0)
            {
                return false;
            }

            cursor = at + segment.Length;
        }

        return MatchTail(text, segments[^1], cursor);
    }

    /// <summary>The wildcard-free head (before the first '%') anchors at the start of the text.</summary>
    private static int MatchHead(string text, string head) =>
        head.Length == 0 || (head.Length <= text.Length && SegmentMatches(text, 0, head))
            ? head.Length
            : -1;

    /// <summary>The wildcard-free tail (after the last '%') anchors at the end of the text.</summary>
    private static bool MatchTail(string text, string tail, int cursor)
    {
        if (tail.Length == 0)
        {
            return true;
        }

        var anchor = text.Length - tail.Length;
        return anchor >= cursor && SegmentMatches(text, anchor, tail);
    }

    /// <summary>Finds the first position at or after <paramref name="from"/> where the segment matches.</summary>
    private static int FindSegment(string text, int from, string segment)
    {
        for (var start = from; start <= text.Length - segment.Length; start++)
        {
            if (SegmentMatches(text, start, segment))
            {
                return start;
            }
        }

        return -1;
    }

    /// <summary>Whether a fixed pattern segment matches the text at exactly <paramref name="start"/>.</summary>
    private static bool SegmentMatches(string text, int start, string segment)
    {
        for (var i = 0; i < segment.Length; i++)
        {
            if (segment[i] != '_' && segment[i] != text[start + i])
            {
                return false;
            }
        }

        return true;
    }
}
