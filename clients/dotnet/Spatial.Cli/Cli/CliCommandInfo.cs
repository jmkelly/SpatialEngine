namespace Spatial.Cli;

/// <summary>One documented option of a command, used for help text and validation (ADR-0052).</summary>
public sealed record CliOptionInfo(string Name, string ValueName, string Description, bool Required = false, bool IsFlag = false);

/// <summary>
/// One command the CLI knows about: its group, verb, summary, positional
/// argument help and options. The catalog is the single source for
/// <c>--help</c> and for resolving a typed <see cref="CliArguments"/>
/// (ADR-0052).
/// </summary>
public sealed record CliCommandInfo(
    string Group,
    string Verb,
    string Summary,
    string Arguments,
    IReadOnlyList<CliOptionInfo> Options);
