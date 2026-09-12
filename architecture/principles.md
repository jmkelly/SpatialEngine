# Architecture Principles

Read this when proposing, reviewing or challenging any architectural change.
The ADRs in `architecture/decisions/` are the source of truth; they record
decisions and their status.

## The twenty principles

1. Geometry is core. Spatial algorithms are not.
2. The engine is headless. Every UI is a client.
3. The browser workbench is the frontend.
4. Packaging is not architecture: the host ships standalone.
5. The .NET host runs independently of every client.
6. Contracts outlive implementations.
7. Plugins depend on contracts, never on other plugin implementations.
8. No plugin-specific geometry object crosses a capability boundary.
9. Core geometry values are immutable.
10. Data stores are providers, not the domain model.
11. Long-running operations are jobs and are always cancellable.
12. Plugin code is disposable. Persistent state is external.
13. Open formats and language-neutral protocols are preferred at boundaries.
14. Agents and human clients use the same public capabilities.
15. Optimised provider pushdown is optional and preserves contract semantics.
16. Every derived result records provenance.
17. The kernel remains small, stable and independently testable.
18. Add another language only where profiling or platform integration justifies it.
19. Do not introduce Native AOT until compatibility is demonstrated.
20. The host and every client are covered by the same conformance tests.

## How to change the architecture

- Change a decision → update the ADR (architecture/decisions/) first.
- Add a capability → versioned contract, conformance fixtures, SDK updates.
- Add a package to a platform project → architecture tests require an ADR
  before the allowlist accepts it.

Enforcement lives in tests/architecture/Spatial.Architecture.Tests — every
rule names the principle or ADR it implements.