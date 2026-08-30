# ADR-0021: Native AOT requires measured benefit and compatibility evidence

Status: Accepted

## Context

AOT may reduce startup and size, but can break dynamic loading, trim
reflection-heavy paths and complicate framework compatibility.

## Decision

The host remains JIT-compiled (ADR-0012). Native AOT is adopted only after:
measurements of JIT startup, memory and package size; identification of
executables that need no dynamic managed loading; passing trimming/AOT
compatibility tests; and a decision record comparing benefit to maintenance
complexity. The main host stays JIT-compiled if AOT compromises plugin or
framework compatibility.

## Consequences

- AOT decisions are evidence-based, never aspirational.
- A future AOT worker coexists with JIT components behind the same contracts.
- Performance baselines exist from early phases to measure against.