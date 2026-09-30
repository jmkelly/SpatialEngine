---
status: superseded
date: 2026-08-30
deciders: maintainer + agent
summary: Language-neutral worker boundaries
superseded-by: ADR-0033
---

# ADR-0013: Worker boundaries are language-neutral

## Context

Plugins may eventually be written in .NET, Rust, WebAssembly, Python or other
languages. .NET types must not leak across process boundaries.

## Decision

All worker boundaries use versioned, language-neutral contracts (gRPC and
canonical binary interchange, ADR-0020). Never across a public boundary:
`NetTopologySuite.Geometry`, `Npgsql` types, EF entities, ASP.NET request
objects, Tauri/Rust types or renderer objects.

## Consequences

- Non-.NET providers are possible without protocol changes.
- Contract evolution is explicit and versioned, never implicit.
- The runtime and SDKs are generated or hand-kept in lockstep per language.