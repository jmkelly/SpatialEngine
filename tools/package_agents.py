#!/usr/bin/env python3
"""Fail when a package that owns a hazard carries no local agent guidance, or carries a long one.

    python3 tools/package_agents.py                    # this checkout
    python3 tools/package_agents.py --root /some/where # another tree (tests)

Nested `AGENTS.md` covered three of the fifty projects, and the three were the
pure boundaries — `Spatial.Core`, `Spatial.Contracts`, the architecture tests
— where an inclusion test is genuinely the right content. Everything that owns
a hazard had none: raw SQL where a half-applied create is the failure, byte formats
where the codec source is the only spec, font
embedding and raster ownership, datum-shift grid
re-composition, and the
whole TypeScript surface. `AGENTS.md` resolves nearest-file-wins, so those
files are what an agent reads when it edits the risky code.

Ten files, each of which would go stale the week after it was written, is the
failure mode a prose document has and a derived check does not. These are the
rules that hold:

1.  **Every listed package has a nested `AGENTS.md`.** The list is the packages
    that own a hazard, and it is named here rather than inferred, because a
    gate over "every project" would force thirty lines onto twenty projects
    that have nothing local to say.
2.  **Each carries a never-list** — the local prohibitions, not the principles
    `AGENTS.md` already states. The repo-level walls are inherited by being
    nearest; what belongs here is only what is local.
3.  **Each carries its test command**, so a change here is verified the way the
    package is verified rather than the way the reader assumes.
4.  **Each is under thirty lines.** Under the
    root file's own bloat threshold a second context file is read; over it,
    it is skimmed.

**This is a check the lanes call directly**, next to
`tools/trailing_whitespace.py`, `tools/conflict_markers.py` and
`tools/doc_surface.py`, and not a `tools/test_*.py` the tooling suite happens
to discover: both `eng/verify.sh` and `tools/verify_scope.py` run the tooling
suite only when the change set touches `tools/**`, and a nested `AGENTS.md` is
exactly the `src/**` change that would go unguarded.
`tools/test_package_agents.py` keeps the unit tests over the
rules and pins the wiring.

It reads the ten files, changes nothing, has no
`--fix`, and needs no .NET SDK, so it runs in a checkout with no toolchain.
"""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

#: The packages that own a hazard, and therefore the ones whose `AGENTS.md` has
#: to exist. Ordered as `tools/package_agents.py`'s docstring orders them, so a
#: failure reads in that order. `Spatial.Core`, `Spatial.Contracts` and the
#: architecture tests already carry one and are not repeated: they own no
#: hazard, they are the boundaries the walls are about.
PACKAGES = (
    "src/Spatial.Stores.PostGIS",
    "src/Spatial.Stores.SqlServer",
    "src/Spatial.Adapter.GeoServices",
    "src/Spatial.Tiling.Mvt",
    "src/Spatial.Esri.Codec",
    "src/Spatial.Ingest.Codec",
    "src/Spatial.Imagery.Vips",
    "src/Spatial.Rendering.Skia",
    "src/Spatial.Transformations.ProjNet",
    "clients/typescript",
    "apps/workbench-web",
)

#: The budget from rule 4. The root `AGENTS.md` sets the bloat threshold for a
#: context file; a nested one has less to say and a nearer reader.
MAX_LINES = 30

NEVER_HEADING = re.compile(r"^##\s+Never\b", re.MULTILINE)
COMMANDS_HEADING = re.compile(r"^##\s+Commands\b", re.MULTILINE)
TEST_COMMAND = re.compile(r"^\s*[-*]?\s*`?[^`\n]*\b(?:dotnet|npm)\s+test\b", re.MULTILINE)

def findings(root: Path) -> list[str]:
    found: list[str] = []
    for package in PACKAGES:
        path = root / package / "AGENTS.md"
        if not path.is_file():
            found.append(f"{package}: no nested AGENTS.md — the package owns a hazard and the file is what an agent reads")
            continue
        body = path.read_text(encoding="utf-8")
        lines = len([line for line in body.splitlines() if line.strip()])
        if lines > MAX_LINES:
            found.append(f"{package}/AGENTS.md: {lines} lines, over the {MAX_LINES}-line budget — split the file or say less")
        if not NEVER_HEADING.search(body):
            found.append(f"{package}/AGENTS.md: no `## Never` never-list — the local prohibitions are the content that belongs here")
        if not COMMANDS_HEADING.search(body) or not TEST_COMMAND.search(body):
            found.append(f"{package}/AGENTS.md: no test command — a change here must be verified the way the package is")
    return found


def check(root: Path) -> list[str]:
    return findings(root)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=".", help="repository root (default: the current directory)")
    args = parser.parse_args(argv)
    found = check(Path(args.root).resolve())
    for finding in found:
        print(f"package_agents: {finding}", file=sys.stderr)
    if found:
        print(f"package_agents: {len(found)} finding(s)", file=sys.stderr)
        return 1
    print(f"package_agents: {len(PACKAGES)} hazardous packages, each with a nested AGENTS.md under {MAX_LINES} lines")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
