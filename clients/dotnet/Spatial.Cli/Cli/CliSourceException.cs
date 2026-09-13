namespace Spatial.Cli;

/// <summary>
/// A failure to read an ingest source — a remote <c>http(s)</c> URL the CLI
/// streams for <c>dataset add --url</c>, or a local file. Kept distinct from a
/// host transport failure so the message names the source, not the host.
/// </summary>
public sealed class CliSourceException(string message, Exception? innerException = null)
    : Exception(message, innerException);
