# Plugin Lifecycle

Read when packaging, loading, supervising or replacing plugins. See
implementation-plan.md §10 and ADR-0002/0006.

## Boundaries, in order of preference

1. **Isolated worker process** (default): separate executable, language-neutral
   protocol (ADR-0013), crash isolation, independent restart, resource
   controls, side-by-side versions and draining.
2. **In-process collectible ALC**: only for controlled, latency-sensitive,
   trusted components; not a security boundary.
3. **WebAssembly**: portable, permission-constrained computation, after the
   worker model is established (Phase 12).

## States

```text
Discovered -> Validated -> Starting -> Healthy -> Active
                                             -> Draining -> Stopped
```

## Replacement sequence

1. Validate the new immutable package.
2. Start it beside the active version.
3. Run health and compatibility checks.
4. Route new work to the new version.
5. Drain the previous version.
6. Stop it after current work completes or reaches a cancellation boundary.
7. Retain rollback metadata.

## Rules

- Plugin code is disposable; persistent state is external (principle 12).
- Plugins receive no ambient authority; secrets stay host-managed.
- A replacement must never require a host or UI restart.