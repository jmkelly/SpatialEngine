---
status: superseded
date: 2026-08-30
deciders: maintainer + agent
summary: Tauri contains no spatial logic
superseded-by: ADR-0039
---

# ADR-0017: Tauri contains no spatial business logic

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
