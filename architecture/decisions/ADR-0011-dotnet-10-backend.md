# ADR-0011: .NET 10 is the initial backend and runtime

Status: Accepted

## Context

The spatial host, runtime and first-party plugin SDK need a mature,
performant managed runtime with strong gRPC, ASP.NET Core and packaging
support.

## Decision

.NET 10 is the backend and runtime for the initial spatial host, the .NET
plugin worker SDK and first-party plugins. Worker boundaries remain
language-neutral (ADR-0013) so other languages can join later.

## Consequences

- One toolchain (SDK 10.0.400, pinned in `global.json`) for the platform.
- Framework and language features of .NET 10 are available to all projects.
- Future non-.NET workers are isolated behind the same contracts.