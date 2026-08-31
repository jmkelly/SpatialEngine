namespace Spatial.Host;

/// <summary>
/// The host's structured startup diagnostics (actionable, never secret-bearing):
/// plugin package activation outcomes. LoggerMessage delegates keep logging
/// allocation-free and honour the configured log level (CA1848/CA1873).
/// </summary>
internal static partial class HostLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Activated plugin package {Package} from {Path}.")]
    public static partial void PackageActivated(ILogger logger, string package, string path);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Plugin package {Package} at {Path} failed to activate ({Message}); continuing.")]
    public static partial void PackageActivationFailed(
        ILogger logger, Exception exception, string package, string path, string message);
}