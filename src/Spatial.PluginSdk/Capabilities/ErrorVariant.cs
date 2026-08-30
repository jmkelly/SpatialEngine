namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// One declared failure mode of a capability (plan §9 "error variants").
/// The code is a stable, dotted lowercase identifier (for example
/// <c>invalid.arguments</c>); the description explains when the error occurs.
/// A capability must declare at least one error variant.
/// </summary>
public sealed record ErrorVariant
{
    public ErrorVariant(string code, string description)
    {
        var problem = QualifiedNameRules.DescribeProblem(code);
        if (problem is not null)
        {
            throw new ArgumentException($"Not an error code: {problem}", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("An error variant needs a description of when the error occurs.", nameof(description));
        }

        Code = code;
        Description = description;
    }

    /// <summary>Stable error code, for example <c>invalid.arguments</c>.</summary>
    public string Code { get; }

    /// <summary>When this failure mode occurs.</summary>
    public string Description { get; }

    public override string ToString() => Code;
}
