# Architecture tests (tests/architecture)

Dependency-free guardrails that parse `.csproj`, `.slnx` and `package.json`
files from a clean checkout — no build required. Every rule implements one
principle or ADR and names it in its summary comment.

## Changing a rule

1. Update the architecture plan or ADR that justifies the rule first —
   a rule without a named decision is a policy nobody agreed to.
2. Edit the rule (or `RepositoryScanner`) in this project.
3. Run `dotnet test` here; violations must be real, not stale (`dotnet build`
   is not a prerequisite, so failures always mean the declared structure
   broke a boundary).
4. Keep scanner failures actionable: each violation message names the file,
   the offending reference and the rule being enforced.

## Adding packages to platform projects

The platform allowlist (`AllowedPackages` in `ArchitectureGuardTests`) is the
only escape hatch to the framework-only rule. Adding an entry without an ADR
fails review — the test message says so.