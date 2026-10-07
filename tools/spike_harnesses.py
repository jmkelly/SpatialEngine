#!/usr/bin/env python3
"""Fail when a harness under `eng/` no longer compiles.

    python3 tools/spike_harnesses.py                    # build every ungated harness
    python3 tools/spike_harnesses.py --list             # name them, needs no SDK
    python3 tools/spike_harnesses.py --root /some/where # another checkout
    python3 tools/spike_harnesses.py --dotnet ./fake    # a `dotnet` stand-in (tests)

`eng/spike-u2x-query-baseline` and `eng/spike-u2x-tile-cache` are measurement
harnesses: committed, runnable, and deliberately **not** in
`SpatialEngine.slnx`, so the build, coverage and metrics gates stay exactly as
they were. That is the right call for coverage and the wrong one for
compilation, because nothing else compiles them either:

* `tools/verify_scope.py` derives the fast lane's build list by walking the
  `ProjectReference` graph **from the solution's own project list**. A project
  no solution project references is not in that graph, so it is in no lane's
  build;
* no lane builds `eng/**` by path, and the harnesses have no test project — each
  csproj says `<IsTestProject>false</IsTestProject>` so `dotnet test` ignores it.

So a harness can drift out of compilation as the engine moves under it, and
nothing says so. On 2026-10-02 `eng/spike-u2x-query-baseline` had stopped
compiling entirely: `EsriFilterClause` had become `EsriWhere`,
`IFeatureStore.QueryAsync` had taken a `FeatureQuery` and answered a
`FeatureQueryPage`, and path C threw where it had used to answer empty.
`SpatialEngine-58d` had to port it before it could re-measure anything, and the
port took a while — which is the whole cost of a committed harness that does
not still run when the next person needs the number.

The previous answer was a dated note at the top of `RESULTS.md` saying when it
was last ported, and it did not work: it is written by the person whose harness
just rotted, and it records no *next* change to `Spatial.Core`.

**This is a check the lanes call directly**, beside
`tools/trailing_whitespace.py` and `tools/conflict_markers.py` (ADR-0143,
ADR-0146, ADR-0148), not a test the tooling suite happens to discover: that
suite runs only when the change set touches `tools/**`, and the change that
breaks a harness is a `src` change. `tools/test_spike_harnesses.py` keeps the
unit tests over the discovery, the build contract and the wiring (ADR-0190).

The set is **derived, not listed**: every `*.csproj` under `eng/` that
`SpatialEngine.slnx` does not name. A harness added tomorrow is gated without
anybody remembering a list, and a harness moved into the solution stops being
built here rather than being built twice. An unreadable solution is treated as
naming nothing, so the check cannot fail open; a `dotnet` that is not there is
a failure rather than a silent pass.

The build is the one part that needs the .NET SDK. `--list` is the half that
does not, so the wiring can be read on a box with no toolchain.
"""
from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path

DEFAULT_REPO = Path(__file__).resolve().parent.parent

#: The solution the rest of the gate builds. A project it names is built by
#: every lane that builds the solution, so this check does not build it again.
SOLUTION_FILE = "SpatialEngine.slnx"

#: Where the harnesses live. Everything the repository builds that is not in
#: the solution lives here: a measurement harness under `eng/` is code the
#: repository ships, and the gate compiles code the repository ships.
HARNESS_ROOT = "eng"

#: Regenerated copies of a project, which are not projects.
IGNORED_DIR_NAMES = frozenset({"bin", "obj", ".git", ".vs", "node_modules"})


def solution_projects(root: Path) -> set[str]:
    """The project paths `SpatialEngine.slnx` names, as repository-relative.

    A missing or unreadable solution reads as naming nothing rather than as
    naming everything: the failure this check exists for is a harness silently
    left out of every build, so an unreadable answer must widen the set, not
    narrow it.
    """
    solution = root / SOLUTION_FILE
    if not solution.is_file():
        return set()
    marker = '<Project Path="'
    projects = set()
    for line in solution.read_text(encoding="utf-8").splitlines():
        start = line.find(marker)
        if start < 0:
            continue
        value = line[start + len(marker):]
        end = value.find('"')
        if end > 0:
            projects.add(value[:end])
    return projects


def harness_projects(root: Path) -> list[str]:
    """Every `*.csproj` under `eng/`, as sorted repository-relative paths."""
    base = root / HARNESS_ROOT
    if not base.is_dir():
        return []
    found = [
        path.relative_to(root).as_posix()
        for path in base.rglob("*.csproj")
        if not IGNORED_DIR_NAMES & set(path.relative_to(root).parts)
    ]
    return sorted(found)


def ungated_harnesses(root: Path) -> list[str]:
    """The harnesses no solution builds: `eng/**/*.csproj` minus the slnx list."""
    named = solution_projects(root)
    return [project for project in harness_projects(root) if project not in named]


def build(harnesses: list[str], root: Path, dotnet: str) -> int:
    """Build each harness. Returns the number that failed to build."""
    failures = 0
    for project in harnesses:
        print(f"   {project}")
        result = subprocess.run(
            [dotnet, "build", project],
            cwd=str(root),
        )
        if result.returncode != 0:
            # The compiler's own output went to the terminal above; the line
            # here says *which* harness, which the output alone does not.
            print(f"spike-harness: {project} does not compile "
                  f"(dotnet build exited {result.returncode})", file=sys.stdout)
            failures += 1
    return failures


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Fail when a harness under eng/ no longer compiles.")
    parser.add_argument("--root", type=Path, default=DEFAULT_REPO,
                        help="repository root (default: the checkout holding this script)")
    parser.add_argument("--dotnet", default="dotnet",
                        help="the dotnet to build with (default: `dotnet` on PATH)")
    parser.add_argument("--list", action="store_true",
                        help="print the ungated harnesses and build nothing")
    args = parser.parse_args(argv)
    root = args.root.resolve()
    harnesses = ungated_harnesses(root)
    if args.list:
        for project in harnesses:
            print(project)
        return 0
    print(f"   {len(harnesses)} harness(es) under {HARNESS_ROOT}/ that no solution builds")
    if not harnesses:
        return 0
    resolved = shutil.which(args.dotnet)
    if resolved is None:
        # A check that could not run must not read as a clean gate: the whole
        # class of false green ADR-0143 and ADR-0146 are about.
        print(f"spike-harness: {args.dotnet} is not on PATH, so the harnesses "
              f"were not compiled: {', '.join(harnesses)}", file=sys.stdout)
        return 1
    return 1 if build(harnesses, root, resolved) else 0


if __name__ == "__main__":
    raise SystemExit(main())
