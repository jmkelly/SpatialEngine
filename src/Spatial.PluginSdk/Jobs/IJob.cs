using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Jobs;

/// <summary>
/// The read-only handle to a running or finished job (ADR-0008): the
/// invocation view the job serves (via <see cref="IInvocationContext"/>), its
/// state, its append-only events, and the two operations clients need —
/// wait for the terminal result and cancel. Every job reaches a terminal
/// state; <see cref="WaitForCompletionAsync"/> returns the same
/// <see cref="CapabilityResult"/> shape inline invocations return.
/// </summary>
public interface IJob : IInvocationContext
{
    JobId Id { get; }

    JobState State { get; }

    DateTimeOffset CreatedAt { get; }

    /// <summary>When the job reached its terminal state, or null while it runs.</summary>
    DateTimeOffset? CompletedAt { get; }

    /// <summary>Append-only event snapshot: lifecycle, progress, published resources and diagnostics.</summary>
    IReadOnlyList<JobEvent> Events { get; }

    /// <summary>Waits for the job's terminal result (the completion of its invocation).</summary>
    ValueTask<CapabilityResult> WaitForCompletionAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests cancellation of the running invocation (jobs are always cancellable, ADR-0008).</summary>
    ValueTask CancelAsync(CancellationToken cancellationToken = default);
}
