namespace Spatial.SqlServer.Tests;

/// <summary>
/// The outcome of asking for a container: either the container, or the reason
/// in words the suite's skip lines carry.
/// </summary>
/// <param name="Started">Whether a container is ready to use.</param>
/// <param name="Container">The container, when one started.</param>
/// <param name="Attempts">How many attempts were made before this answer.</param>
/// <param name="Reason">
/// Why the container is not there, in the words <c>Skip.If</c> shows. Null when
/// the container started.
/// </param>
/// <param name="TimedOut">
/// Whether the last attempt ran out of its time budget rather than being
/// refused — the two are different facts about the host, and a lane reading
/// the reason can tell a loaded box from an absent daemon.
/// </param>
public sealed record SqlServerStartResult<TContainer>(
    bool Started,
    TContainer? Container,
    int Attempts,
    string? Reason,
    bool TimedOut)
    where TContainer : class;
