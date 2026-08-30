# ADR-0024: Jobs are observable state machines with events and timeouts

Status: Accepted

## Context

ADR-0008 says long-running operations are jobs: tracked by a job id,
reporting status, progress and events, always cancellable, subject to
timeouts. Phase 3 declared the `LongRunning` trait and required it to be
cancellable; Phase 4 must give the trait its runtime behaviour and a
single observable surface for clients that poll or subscribe.

## Decision

A job is a tracked state machine `Pending → Running → terminal`, where the
terminal state is `Completed`, `Failed`, `Cancelled` or `TimedOut`. Jobs:

- are created by every long-running invocation — `InvokeAsync` routes
  `LongRunning` capabilities through a job internally (the outcome carries
  the job id), and `StartJob` returns the handle immediately for clients
  that want to poll or subscribe;
- record append-only events (`JobEvent`) using the existing contract
  surfaces — `ProgressReport` for progress, `ICapabilityError` for
  failures, `ResourceHandle` for published resources (so a long stream's
  handle is discoverable while the job runs);
- expose the invocation view through `IJob : IInvocationContext`, so the
  job handle is the same read-only surface providers already depend on;
- always link the caller token, the job's own cancellation and the deadline
  into one effective token; when the deadline fires the job ends `TimedOut`
  with `deadline.exceeded` (attributed without wall-clock races — if the
  caller token and the job token are both quiet, the cancellation came from
  the deadline);
- fail structured (`Failed` + `ICapabilityError`) for provider, contract
  and pre-check failures; every request that reaches the runtime still
  yields a tracked job id, so pre-check failures produce jobs already in
  their terminal state.

## Consequences

- Progress and cancellation are uniform across providers and operations —
  one job API for polling, subscription and reporting.
- Every long-running behaviour has a cancellation path and a timeout path
  (agent rule), both tested.
- Hosts can retain or prune finished jobs (`JobRegistry.Forget`); HTTP and
  SDK surfaces in Phase 9 expose the same model.