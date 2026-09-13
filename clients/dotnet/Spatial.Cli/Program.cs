namespace Spatial.Cli;

/// <summary>
/// The entry point of the Spatial CLI (ADR-0052). Kept as a named type with
/// a named method rather than top-level statements so the quality gates see
/// a real <c>Main</c> method (not the compiler-merged <c>Program.&lt;Main&gt;$</c>).
/// </summary>
internal static class Program
{
    private static Task<int> Main(string[] args) => CliApplication.RunAsync(args, SystemConsole.Instance);
}
