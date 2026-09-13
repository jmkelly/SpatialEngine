namespace Spatial.Cli;

/// <summary>
/// A command-line usage failure: a missing/unknown option, an unknown
/// command, or a value the command cannot accept. Mapped to
/// <see cref="ExitCodes.Usage"/> and printed without a stack trace
/// (ADR-0052).
/// </summary>
public sealed class CliUsageException : Exception
{
    public CliUsageException(string message)
        : base(message)
    {
    }
}
