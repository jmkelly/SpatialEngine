namespace Spatial.Core.Features;

/// <summary>
/// One feature's temporal extent: the interval between the two date fields its
/// layer designates, with inclusive bounds and a null bound read as infinite
/// (ADR-0175). Structural: two bounds compared, read off an attribute bag —
/// no transport, no store, no service vocabulary. The three interval
/// relations over it are the whole of the temporal model, and the readers
/// (query, identify, render) share them rather than each spelling one out.
/// </summary>
/// <param name="Start">The feature's start instant, or <c>null</c> for infinite.</param>
/// <param name="End">The feature's end instant, or <c>null</c> for infinite.</param>
public readonly record struct TemporalExtent(DateTimeOffset? Start, DateTimeOffset? End)
{
    /// <summary>
    /// The requested window in epoch milliseconds, the vocabulary both request
    /// sides already speak (a <c>null</c> bound open, an instant has equal
    /// bounds).
    /// </summary>
    public static TemporalExtent FromMilliseconds(long? startMs, long? endMs) => new(
        startMs is { } start ? DateTimeOffset.FromUnixTimeMilliseconds(start) : null,
        endMs is { } end ? DateTimeOffset.FromUnixTimeMilliseconds(end) : null);

    /// <summary>
    /// The extent a feature carries under a layer's designation. A designated
    /// field that is null — or that the schema does not have — reads as an
    /// infinite bound, so a row with neither bound is the whole line and every
    /// relation holds for it. The bounds are never swapped: a start after the
    /// end is an empty extent, not an invented interval.
    /// </summary>
    public static TemporalExtent From(IFeature feature, TemporalExtentFields fields)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ArgumentNullException.ThrowIfNull(fields);

        return new TemporalExtent(Bound(feature, fields.StartField), Bound(feature, fields.EndField));
    }

    /// <summary>
    /// The rule an <em>undesignated</em> layer keeps (ADR-0100): the feature
    /// matches when any of its date values falls inside the window, and a
    /// feature with no date value matches unconditionally — ArcGIS Server
    /// ignores <c>time</c> on a layer with no time-aware field.
    /// </summary>
    public static bool MatchesAnyDate(IFeature feature, TemporalExtent window)
    {
        ArgumentNullException.ThrowIfNull(feature);

        var dated = false;
        foreach (var attribute in feature.Attributes)
        {
            if (attribute.Kind != AttributeKind.DateTimeOffset)
            {
                continue;
            }

            dated = true;
            if (Inside(attribute.DateTimeOffsetValue, window))
            {
                return true;
            }
        }

        return !dated;
    }

    /// <summary>Whether the extent holds the window.</summary>
    public bool Contains(TemporalExtent window) => Matches(window, TemporalRelation.Contains);

    /// <summary>Whether the window holds the extent.</summary>
    public bool Within(TemporalExtent window) => Matches(window, TemporalRelation.Within);

    /// <summary>Whether the extent and the window share an instant.</summary>
    public bool Overlaps(TemporalExtent window) => Matches(window, TemporalRelation.Overlaps);

    /// <summary>
    /// One relation, over inclusive bounds. An empty extent (a start after its
    /// end) matches nothing at all.
    /// </summary>
    public bool Matches(TemporalExtent window, TemporalRelation relation)
    {
        if (IsEmpty())
        {
            return false;
        }

        return relation switch
        {
            TemporalRelation.Overlaps => OverlapsWindow(window),
            TemporalRelation.Contains => ContainsWindow(window),
            TemporalRelation.Within => WithinWindow(window),
            _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, "Unknown temporal relation."),
        };
    }

    /// <summary>Whether the extent is empty: a start after its end, matching nothing at all.</summary>
    private bool IsEmpty() => Start is { } start && End is { } end && start > end;

    private bool OverlapsWindow(TemporalExtent window) =>
        NotAfter(Start, window.End) && NotBefore(End, window.Start);

    private bool ContainsWindow(TemporalExtent window) =>
        NotAfter(Start, window.Start) && NotBefore(End, window.End);

    private bool WithinWindow(TemporalExtent window) =>
        NotBefore(Start, window.Start) && NotAfter(End, window.End);

    /// <summary>One instant against the window's inclusive bounds, a null bound infinite.</summary>
    private static bool Inside(DateTimeOffset value, TemporalExtent window) =>
        NotBefore(value, window.Start) && NotAfter(value, window.End);

    private static bool NotAfter(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null || right is null || left <= right;

    private static bool NotBefore(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null || right is null || left >= right;

    private static DateTimeOffset? Bound(IFeature feature, string? field)
    {
        if (field is null)
        {
            return null;
        }

        var index = feature.Schema.IndexOf(field);
        return index < 0 || feature[index].Kind != AttributeKind.DateTimeOffset
            ? null
            : feature[index].DateTimeOffsetValue;
    }
}

/// <summary>
/// The three interval relations a temporal extent can be asked about. The
/// names are the model's, not a client's: a surface's own spelling of one is
/// translated where it is parsed.
/// </summary>
public enum TemporalRelation
{
    /// <summary>The extent and the window share an instant.</summary>
    Overlaps,

    /// <summary>The extent covers the window.</summary>
    Contains,

    /// <summary>The window covers the extent.</summary>
    Within,
}
