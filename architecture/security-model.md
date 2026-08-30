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

## Consequences for code

- Connection strings flow from host configuration into providers at launch,
  never through the web client.
- Permission evaluation lives in the runtime (Phase 3), attached to
  capabilities and jobs.
- Every provider implementation has structured diagnostics; secrets never
  appear in diagnostics.