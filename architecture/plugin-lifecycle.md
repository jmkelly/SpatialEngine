# Plugin Lifecycle

Read when packaging, loading, supervising or replacing plugins. See
implementation-plan.md §10/§16 (Phase 5) and ADR-0002/0006/0025.

## Boundaries, in order of preference

1. **Isolated worker process** (default): separate executable, language-neutral
   protocol (ADR-0013/0025), crash isolation, independent restart, resource
   controls, side-by-side versions and draining.
2. **In-process collectible ALC**: only for controlled, latency-sensitive,
   trusted components; not a security boundary.
3. **WebAssembly**: portable, permission-constrained computation, after the
   worker model is established (Phase 12).

## States

```text
Discovered -> Validated -> Starting -> Healthy -> Active
                                             -> Draining -> Stopped
                                             -> Failed (restart policy exhausted)
```

A crashed worker transitions through a supervised restart back to `Active`;
a worker that exhausts its restart policy (or never handshakes) is `Failed`
until a human or host re-activates it.

## Packaging

A plugin package is an immutable directory: `manifest.json` (schema v1 —
`architecture/plugin-manifest.md`) plus its loadable payload. Packages are
discovered by scanning a packages root; side-by-side versions are sibling
package directories. A blueprint that diverges from the loaded provider's
contract surface is refused at activation (manifest compatibility check).

## Supervision (`Spatial.PluginHost.DotNet`)

- **Activation**: spawn the worker host apphost with `--package <dir>`, run
  the wire-protocol handshake (`hello`, id + capability set vs the
  manifest), register the worker's provider proxy into the host's
  `CapabilityRegistry` and mark it `Active`. A duplicate provider id is
  refused with an actionable error.
- **Health**: periodic `ping`/`pong`; consecutive failures trip a threshold,
  mark the provider unhealthy (new work skips it) and restart.
- **Restart**: exponential backoff (100 ms doubling, capped), bounded attempts
  (default 3); the registry keeps the provider until a fresh process is
  confirmed healthy. Exhaustion → `Failed` + unhealthy.
- **Side-by-side activation**: activate the new version beside the old; route
  new work with the runtime's active-preference table (`CapabilityRuntime
  .SetActivePreference`) — soft, reversible, consulted between the
  resource-local and configured-preference resolution steps.
- **Draining**: stop routing new work (health → unhealthy), wait for in-flight
  invocations, reclaim the worker's resources through
  `ResourceRegistry.DisposeOwnerAsync` (the draining call site), send
  `close` and wait for `closed`, then stop the process. The host never
  restarts.
- **Rollback**: the previous version is kept running during the replacement;
  rolling back simply routes new work back to it.

## Replacement sequence

1. Validate the new immutable package.
2. Start it beside the active version.
3. Run health and compatibility checks.
4. Route new work to the new version (active preference).
5. Drain the previous version (wait for in-flight work, reclaim resources).
6. Stop it after current work completes or reaches a cancellation boundary.
7. Retain rollback metadata (the still-running previous package).

## Rules

- Plugin code is disposable; persistent state is external (principle 12).
- Plugins receive no ambient authority; secrets stay host-managed.
- A replacement must never require a host or UI restart.
- Worker types never cross the public boundary (ADR-0025); only core and SDK
  contract types do.