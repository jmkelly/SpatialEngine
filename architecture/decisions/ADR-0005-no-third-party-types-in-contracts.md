---
status: accepted
date: 2026-08-30
deciders: maintainer + agent
summary: Third-party types (NTS, Npgsql, EF, renderer) never cross public contracts.
---

# ADR-0005: Third-party geometry types do not cross contracts

## Context

NetTopologySuite, Npgsql, renderer and ORM types are convenient internally
but bind every consumer to one implementation. Public boundaries must stay
language-neutral (ADR-0013) and implementation-neutral.

## Decision

Only core geometry values cross public boundaries. Adapters convert
third-party types at the edges of their owning plugin, privately
(architecture tests enforce the package allowlist and reference rules).

## Consequences

- Contracts outlive implementations; NTS could be replaced behind its plugin.
- Plugins own their conversion logic and test it with conformance fixtures.
- No renderer, provider or ORM type appears in the public API surface.
