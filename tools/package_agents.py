#!/usr/bin/env python3
"""Fail when a package that owns a hazard carries no local agent guidance, or carries a long one.

    python3 tools/package_agents.py                    # this checkout
    python3 tools/package_agents.py --root /some/where # another tree (tests)

Nested `AGENTS.md` covered three of the fifty projects, and the three were the
pure boundaries — `Spatial.Core`, `Spatial.Contracts`, the architecture tests
— where an inclusion test is genuinely the right content. Everything that owns
a hazard had none: raw SQL (ADR-0092 makes index creation part of
create-or-rollback, so a half-applied create is the failure), byte formats
where the codec source is the only spec (ADR-0020, ADR-0029, ADR-0032), font
embedding and raster ownership (ADR-0049, ADR-0051), datum-shift grid
re-composition (ADR-0105, where composing twice cost 113 m at London), and the
whole TypeScript surface. `AGENTS.md` resolves nearest-file-wins, so those
files are what an agent reads when it edits the risky code.

Ten files, each of which would go stale the week after it was written, is the
failure mode a prose document has and a derived check does not. These are the
five rules that hold:

1.  **Every listed package has a nested `AGENTS.md`.** The list is the packages
    that own a hazard, and it is named here rather than inferred, because a
    gate over "every project" would force thirty lines onto twenty projects
    that have nothing local to say.
2.  **Each names at least two binding records by number**, and every number it
    cites resolves to a record on disk. One ADR is a preference; two is the
    pair that decided the package, and a citation that dangles sends the
    reader nowhere.
3.  **Each carries a never-list** — the local prohibitions, not the principles
    `AGENTS.md` already states. The repo-level walls are inherited by being
    nearest; what belongs here is only what is local.
4.  **Each carries its test command**, so a change here is verified the way the
    package is verified rather than the way the reader assumes.
5.  **Each is under thirty lines**, counting everything the file says except
    the generated orientation block `tools/orientation.py` owns. Under the
    root file's own bloat threshold a second context file is read; over it,
    it is skimmed.

**This is a check the lanes call directly**, next to
`tools/trailing_whitespace.py`, `tools/conflict_markers.py` and
`tools/doc_surface.py`, and not a `tools/test_*.py` the tooling suite happens
to discover: both `eng/verify.sh` and `tools/verify_scope.py` run the tooling
suite only when the change set touches `tools/**`, and a nested `AGENTS.md` is
exactly the `src/**` change that would go unguarded (the ADR-0148 argument,
the second time). `tools/test_package_agents.py` keeps the unit tests over the
five rules and pins the wiring.

It reads the ten files and the record filenames, changes nothing, has no
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

#: The budget from rule 5. The root `AGENTS.md` sets the bloat threshold for a
#: context file; a nested one has less to say and a nearer reader.
MAX_LINES = 30

#: How many records bind a package (rule 2). One is a preference.
MIN_ADRS = 2

CITATION = re.compile(r"ADR-(\d{4})")
NEVER_HEADING = re.compile(r"^##\s+Never\b", re.MULTILINE)
COMMANDS_HEADING = re.compile(r"^##\s+Commands\b", re.MULTILINE)
TEST_COMMAND = re.compile(r"^\s*[-*]?\s*`?[^`\n]*\b(?:dotnet|npm)\s+test\b", re.MULTILINE)
ROUTE_ROW = re.compile(r"architecture/distilled/[A-Za-z0-9_-]+\.md")

ORIENTATION_BEGIN = "<!-- orientation:begin -->"
ORIENTATION_END = "<!-- orientation:end -->"


def body_of(text: str) -> str:
    """The file minus the orientation block `tools/orientation.py` generates."""
    start = text.find(ORIENTATION_BEGIN)
    if start == -1:
        return text
    end = text.find(ORIENTATION_END, start)
    if end == -1:
        return text[:start]
    return text[:start] + text[end + len(ORIENTATION_END):]


def record_numbers(root: Path) -> set[str]:
    """Every ADR number that carries a record on disk, as four digits."""
    decisions = root / "architecture" / "decisions"
    return {p.name.split("-")[1] for p in decisions.glob("ADR-*.md")}


def findings(root: Path) -> list[str]:
    known = record_numbers(root)
    found: list[str] = []
    for package in PACKAGES:
        path = root / package / "AGENTS.md"
        if not path.is_file():
            found.append(f"{package}: no nested AGENTS.md — the package owns a hazard and the file is what an agent reads")
            continue
        text = path.read_text(encoding="utf-8")
        body = body_of(text)
        lines = len([line for line in body.splitlines() if line.strip()])
        if lines > MAX_LINES:
            found.append(f"{package}/AGENTS.md: {lines} lines, over the {MAX_LINES}-line budget — split the file or say less")
        cited = sorted(set(CITATION.findall(body)))
        if len(cited) < MIN_ADRS:
            found.append(f"{package}/AGENTS.md: names {len(cited)} binding ADRs, under the {MIN_ADRS} that decide it")
        dangling = [f"ADR-{n}" for n in cited if n not in known]
        if dangling:
            found.append(f"{package}/AGENTS.md: cites {', '.join(dangling)}, which carries no record in architecture/decisions")
        if not NEVER_HEADING.search(body):
            found.append(f"{package}/AGENTS.md: no `## Never` never-list — the local prohibitions are the content that belongs here")
        if not COMMANDS_HEADING.search(body) or not TEST_COMMAND.search(body):
            found.append(f"{package}/AGENTS.md: no test command — a change here must be verified the way the package is")
        if not ROUTE_ROW.search(body):
            found.append(f"{package}/AGENTS.md: no route row — cite the `architecture/distilled/*.md` digest for this task")
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
