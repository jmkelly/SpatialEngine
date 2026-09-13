namespace Spatial.Cli;

/// <summary>
/// Everything one command handler needs (ADR-0052): the resolved settings,
/// typed arguments, output seam and host gateway, plus the group/verb for
/// the JSON envelope.
/// </summary>
public sealed record CliContext(
    string Group,
    string Verb,
    CliSettings Settings,
    CliArguments Arguments,
    CliOutput Output,
    ISpatialGateway Gateway)
{
    /// <summary>The command label used by help, envelopes and errors.</summary>
    public string Command => $"{Group} {Verb}";
}
