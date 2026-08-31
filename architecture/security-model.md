# Security Model

Read when adding provider connections, worker boundaries, native adapters or
anything handling secrets. See implementation-plan.md §19.

## Invariants

- Plugins receive no ambient authority.
- Third-party plugins run out of process or in WebAssembly.
- Secrets remain host-managed and scoped; Tauri never embeds database
  credentials.
- A bundled host binds only to an appropriate local endpoint and validates
  the desktop client connection.
- Native adapters expose narrowly defined capabilities, never arbitrary shell
  execution.
- Input and output contracts are validated.
- Payload, stream, memory and time limits are enforced where supported.
- Audit events and provenance are structured.

## Provider connection secrets (Phase 8, ADR-0028)

- **Connection strings flow from host configuration into providers at
  launch, never through the web client.** The mechanism is the worker
  process environment: the host configures the provider's launch environment
  (`WorkerSupervisorOptions.WorkerEnvironment`) and the provider reads its
  connection settings from it on startup (`SPATIAL_POSTGIS_CONNECTION` for
  `postgis@1`). Invocations carry no connection material; a capability
  argument can never name, override or leak a secret.
- **Redaction is part of the provider's diagnostic contract.** Connection
  strings, passwords and hosts are never logged, never appear in
  `CapabilityError` messages and never ride the wire. An unconfigured
  provider fails with an actionable `provider.unavailable` message that names
  the environment variable (not a secret); connection failures describe the
  configuration by a redacted form (database name/schema only). A redaction
  test asserts that a deliberately broken connection string does not appear
  anywhere in the failure.
- **Per-caller scoping** (different hosts, users, databases per environment)
  is achieved by launching the provider worker with that environment — the
  runtime's lease/ownership model already ties every provider's resources to
  its worker instance.

## Consequences for code

- Permission evaluation lives in the runtime (Phase 3), attached to
  capabilities and jobs.
- Every provider implementation has structured diagnostics; secrets never
  appear in diagnostics.
- Client-supplied query text never becomes SQL structure: the PostGIS
  adapter validates dataset identifiers against a strict grammar, resolves
  filter columns against the discovered schema and binds every literal as a
  parameter (ADR-0028, `architecture/data-provider-contracts.md`).

## HTTP host permissions (Phase 9)

- The HTTP API is the public enforcement point for scoping. A caller declares
  the permission names it wants in the invocation request (`permissions`);
  the host grants those scopes against its own policy and the runtime's
  permission gate (set membership by default — `GrantedPermissionsEvaluator`)
  rejects invocations whose declared requirements are not granted. Secrets
  and connection material never ride the request: the only channel is the
  worker launch environment above.
- The host binds loopback by default in local profiles; a remote deployment
  is a deliberate configuration choice (deployment-profiles.md). The desktop
  shell inherits the same API and enforcement (ADR-0017).