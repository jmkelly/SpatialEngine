# ADR-0012: The initial .NET host is JIT-compiled

Status: Accepted

## Context

Native AOT constrains dynamic loading, reflection and some framework paths.
During initial development, plugin loading, diagnostics and developer
iteration matter more than executable size.

## Decision

The spatial host runs JIT-compiled initially. Native AOT is evaluated later
(ADR-0021) and only for narrowly scoped executables that need no dynamic
managed loading.

## Consequences

- Full dynamic loading and diagnostics are available from day one.
- AOT is a measured, deliberate follow-up, not a bootstrap constraint.
- Host startup and footprint get baselined so AOT can be compared later.