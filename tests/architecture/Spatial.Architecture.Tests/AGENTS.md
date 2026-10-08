# Architecture tests (tests/architecture)

Dependency-free guardrails that parse `.csproj`, `.slnx` and `package.json`
files from a clean checkout — no build required. Every rule implements one
principle in `architecture/principles.md` and names it in its summary comment.

## Changing a rule

1. Update `architecture/principles.md` first where the standing shape changes —
   the code is the documentation.
2. Edit the rule (or `RepositoryScanner`) in this project.
3. Run `dotnet test` here; violations must be real, not stale (`dotnet build`
   is not a prerequisite, so failures always mean the declared structure
   broke a boundary).
4. Keep scanner failures actionable: each violation message names the file,
   the offending reference and the rule being enforced.

## Adding packages to platform projects

The platform allowlist (`AllowedPackages` in `ArchitectureGuardTests`) is the
only escape hatch to the framework-only rule.
