namespace Spatial.Cli;

/// <summary>
/// The CLI's terminal seam, so command handlers are tested against an
/// in-memory writer instead of the real console (ADR-0052).
/// </summary>
public interface ICliConsole
{
    /// <summary>Standard output.</summary>
    TextWriter Out { get; }

    /// <summary>Standard error.</summary>
    TextWriter ErrorWriter { get; }
}

/// <summary>The process console.</summary>
public sealed class SystemConsole : ICliConsole
{
    /// <summary>The shared process console.</summary>
    public static SystemConsole Instance { get; } = new();

    /// <inheritdoc />
    public TextWriter Out => Console.Out;

    /// <inheritdoc />
    public TextWriter ErrorWriter => Console.Error;
}
