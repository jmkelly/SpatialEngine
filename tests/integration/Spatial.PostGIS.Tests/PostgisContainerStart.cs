namespace Spatial.PostGIS.Tests;

/// <summary>
/// How the suite asks for its container (ADR-0010/ADR-0028's honest
/// degradation, as SpatialEngine-o5p found it): a start is given a generous
/// budget and more than one attempt, and a give-up says which of the two
/// happened.
/// </summary>
/// <remarks>
/// The policy is here, apart from Testcontainers, so it can be tested without
/// a Docker daemon — the defect it fixes is a container that could not start,
/// so a test that needed one to prove the retry would have been unable to
/// fail. It is deliberately this suite's own copy rather than the SQL Server
/// suite's <c>SqlServerContainerStart</c> (ADR-0187 §5): the two suites are
/// separate test assemblies, and one of them should not be able to reach into
/// the other's. The policy is the same one, and the reasons say which store
/// they are about.
/// </remarks>
public static class PostgisContainerStart
{
    /// <summary>How many times a start is tried before the suite degrades.</summary>
    public const int DefaultAttempts = 3;

    /// <summary>
    /// How long one attempt may take. A PostGIS image start is tens of seconds
    /// on an idle box and minutes on a loaded one — Testcontainers' own wait
    /// strategy for <c>postgis/postgis:16-3.4</c> gives about a minute — and a
    /// suite that gives up at the former reports most of itself as skipped for
    /// a reason that clears on its own (ADR-0139).
    /// </summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait between attempts.</summary>
    public static TimeSpan DefaultRetryDelay { get; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Starts a container, retrying a failed or out-of-time attempt rather
    /// than degrading on the first one.
    /// </summary>
    /// <param name="startAsync">
    /// Starts one container and returns it, or throws. A start that throws owns
    /// anything it had already created, so nothing is leaked by a retry.
    /// </param>
    /// <param name="prepareAsync">
    /// What the fixture does to a started container before it is of use (here,
    /// nothing — the databases are made per test class). A preparation that
    /// throws discards the container and is retried like a failed start.
    /// </param>
    /// <param name="discardAsync">Discards a container nothing can use.</param>
    /// <param name="attempts">How many attempts before giving up.</param>
    /// <param name="timeout">The budget of one attempt.</param>
    /// <param name="retryDelay">How long to wait between attempts.</param>
    /// <param name="delayAsync">The wait, injected by the tests.</param>
    /// <param name="cancellationToken">Cancels the whole start.</param>
    public static async Task<PostgisStartResult<TContainer>> StartAsync<TContainer>(
        Func<CancellationToken, Task<TContainer>> startAsync,
        Func<TContainer, CancellationToken, Task>? prepareAsync = null,
        Func<TContainer, CancellationToken, Task>? discardAsync = null,
        int attempts = DefaultAttempts,
        TimeSpan? timeout = null,
        TimeSpan? retryDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        CancellationToken cancellationToken = default)
        where TContainer : class
    {
        ArgumentNullException.ThrowIfNull(startAsync);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        var budget = timeout ?? DefaultTimeout;
        Exception? failure = null;
        var timedOut = false;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(budget);

            TContainer? container = null;
            try
            {
                container = await startAsync(deadline.Token);
                if (prepareAsync is not null)
                {
                    await prepareAsync(container, deadline.Token);
                }

                return new PostgisStartResult<TContainer>(true, container, attempt, null, false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // The lane was cancelled: that is not a start that failed, and
                // it is not something to retry into a skip.
                throw;
            }
            catch (OperationCanceledException)
            {
                // The attempt ran out of its own budget: the host is slow or
                // busy, which is worth another attempt and worth saying.
                timedOut = true;
                failure = new TimeoutException($"the start did not finish within {budget}");
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            if (container is not null && discardAsync is not null)
            {
                // A container nothing can use is still worth discarding, and a
                // discard that fails is not a reason to stop retrying.
                try
                {
                    await discardAsync(container, CancellationToken.None);
                }
                catch (Exception)
                {
                    // Carried on: the next attempt is what the lane is waiting for.
                }
            }

            if (attempt < attempts)
            {
                await (delayAsync ?? Task.Delay)(retryDelay ?? DefaultRetryDelay, cancellationToken);
            }
        }

        return new PostgisStartResult<TContainer>(
            false,
            null,
            attempts,
            Reason(failure, attempts, budget, timedOut),
            timedOut);
    }

    /// <summary>
    /// The words a skip line carries. They keep the original PostGIS shape —
    /// the container could not start, and the daemon is the first thing to
    /// check — and add what the attempt learned, so a skip-heavy run is
    /// readable rather than merely honest.
    /// </summary>
    private static string Reason(Exception? failure, int attempts, TimeSpan budget, bool timedOut) => timedOut
        ? $"the PostGIS container could not start within {budget} after {attempts} attempts (is the Docker daemon reachable, and is the host loaded?): {failure?.Message}"
        : $"the PostGIS container could not start after {attempts} attempts (is the Docker daemon reachable?): {failure?.Message}";
}
