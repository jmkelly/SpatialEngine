namespace Spatial.Core.Features.Ingest;

/// <summary>
/// One inferred attribute field and the evidence behind it: which JSON/CSV
/// value kinds were actually observed, and how many values were null. Type
/// inference that cannot say "I saw int, double and a null" is a guess, and a
/// guess in a schema is a column that fails to load weeks later.
/// </summary>
public sealed record InferredField(
    string Name,
    AttributeKind Kind,
    bool Nullable,
    long Nulls,
    IReadOnlyList<AttributeKind> Observed)
{
    public override string ToString()
    {
        var evidence = Observed.Count == 0 ? "no values" : string.Join("+", Observed);
        return $"{Name} → {Kind}{(Nullable ? " nullable" : string.Empty)} (saw {evidence}, {Nulls} null(s))";
    }
}
