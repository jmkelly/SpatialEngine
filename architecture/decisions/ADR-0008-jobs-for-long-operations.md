# ADR-0008: Long operations use the job model

Status: Accepted

## Context

Spatial work can run for seconds or minutes. HTTP requests cannot hold the
connection, and users need progress, cancellation and diagnostics.

## Decision

Long-running operations are jobs: created by an invocation, tracked by a job
id, reporting status, progress and events, and always cancellable. Jobs are
subject to timeouts, permissions and structured diagnostics. Small operations
may complete inline, but the job model is always available.

## Consequences

- Progress and cancellation are uniform across providers and operations.
- Clients poll or subscribe through one job API.
- Every long-running behaviour has a cancellation path (agent rule).