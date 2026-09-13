namespace Spatial.Cli;

/// <summary>
/// The CLI's stable process exit codes (ADR-0052), so an LLM or script can
/// branch on the outcome without parsing text.
/// </summary>
public static class ExitCodes
{
    /// <summary>The command completed.</summary>
    public const int Success = 0;

    /// <summary>An unexpected failure (a bug or an unmapped host error).</summary>
    public const int Failure = 1;

    /// <summary>The arguments were invalid (<c>invalid.arguments</c>).</summary>
    public const int Usage = 2;

    /// <summary>The named dataset or map does not exist (<c>not.found</c>).</summary>
    public const int NotFound = 3;

    /// <summary>The host or its store is unavailable (<c>store.unavailable</c>).</summary>
    public const int Unavailable = 4;

    /// <summary>The caller cancelled the operation.</summary>
    public const int Cancelled = 5;

    /// <summary>Maps a structured host error code to its exit code.</summary>
    public static int ForSpatialCode(string code) => code switch
    {
        "invalid.arguments" => Usage,
        "not.found" => NotFound,
        "store.unavailable" => Unavailable,
        _ => Failure,
    };
}
