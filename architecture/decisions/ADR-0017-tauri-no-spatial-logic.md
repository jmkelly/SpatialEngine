# ADR-0017: Tauri contains no spatial business logic

Status: Superseded by ADR-0039 (desktop packaging abandoned).

## Context

Shells naturally accrete convenience features; spatial logic in the shell
would bypass the engine's contracts, tests and provenance.

## Decision

The Tauri shell contains no geometry logic, spatial operations, data-provider
logic, capability resolution, project-domain behaviour or desktop-only
spatial API. Its native adapters expose narrowly defined capabilities only.

## Consequences

- All spatial behaviour stays behind the public API and its conformance tests.
- The same conformance tests cover browser and desktop delivery.
- Native adapters are permission-bound narrow capabilities, not shell escapes.