namespace Spatial.Core.Features;

/// <summary>
/// Which of a row's date fields bound it: the schema-level designation of a
/// feature's temporal extent (ADR-0175). Either bound may be absent — a
/// designated start with no designated end is <c>[t, +∞)</c> — and naming the
/// same field for both is the point <c>[t, t]</c>. Two absent bounds designate
/// nothing, which is the same as no designation at all.
/// </summary>
/// <param name="StartField">The field holding the row's start instant, or <c>null</c>.</param>
/// <param name="EndField">The field holding the row's end instant, or <c>null</c>.</param>
public sealed record TemporalExtentFields(string? StartField, string? EndField)
{
    /// <summary>Whether the designation names no bound at all.</summary>
    public bool IsEmpty => StartField is null && EndField is null;

    public override string ToString() =>
        IsEmpty ? "(no designation)" : $"[{StartField ?? "-∞"} … {EndField ?? "+∞"}]";
}