#!/usr/bin/env python3
"""Work out what `eng/verify.sh --quick` has to run for the current branch.

    python3 tools/verify_scope.py --list format     # projects to format
    python3 tools/verify_scope.py --list tests      # test projects to run
    python3 tools/verify_scope.py --list tooling    # 1 if the python tests run
    python3 tools/verify_scope.py --list lane --lane unit
    python3 tools/verify_scope.py --list plan       # human-readable summary
    python3 tools/verify_scope.py --base origin/main

The full gate is one flat run: format over the whole solution, build the whole
solution, test all 55 projects, run the python tooling tests. Measured warm on a
12-core box that is ~25 minutes, and agents run it constantly, so almost all of
the cost is overhead rather than signal. The quick lane keeps the same gate for
the work a change can actually affect:

* format only the projects that own a changed file — `dotnet format` on the
  whole solution is ~30% of the full gate, and almost none of it is analyzers
  (`--diagnostics IDE0055` over the solution is no faster than the default);
  what costs is loading every project through MSBuild, which is per project;
* run only the test projects that reference a changed project, walked over the
  `ProjectReference` graph, so a change to `Spatial.Core` still runs the tests
  that reach it through two hops and a change to `Spatial.Maps` does not run
  the 22-minute host suite;
* always run `Spatial.Architecture.Tests` — 35 s, and it is the structural
  guard that no project grew a contract dependency it should not have, so it is
  worth its cost on every iteration;
* run the python tooling tests only when `tools/**` changed.

The scoping is derived from the change set, and the change set is derived from
git, so a lane that cannot see the change set must not quietly report success
by testing nothing. Every git command here returns a flag next to its output;
when any of them fails, the plan comes back `exhaustive` and `eng/verify.sh`
runs the full gate instead of a narrow guess.
"""
from __future__ import annotations

import argparse
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

#: Always in the quick lane. Cheap, and the structural guard.
ARCHITECTURE_PROJECT = (
    "tests/architecture/Spatial.Architecture.Tests/Spatial.Architecture.Tests.csproj")

#: Files whose project has to be format-checked when they change. A README or a
#: shell script under a project directory changes no formatting.
FORMATTABLE_SUFFIXES = frozenset({".cs", ".csproj", ".props", ".targets",
                                 ".editorconfig", ".resx"})

#: The python tooling tests read this directory; nothing else in the repo does.
TOOLING_PREFIX = "tools/"

#: Files that change what every project builds against, and so belong to no
#: single project. A branch that touches one of them has changed the build for
#: the whole solution, so the quick lane declines to guess and says `exhaustive`.
SOLUTION_WIDE_FILES = frozenset({
    "SpatialEngine.slnx", "global.json", "Directory.Build.props",
    "Directory.Build.targets", "Directory.Packages.props", "NuGet.config",
    "Directory.Build.props.template",
})

#: Directories that never hold a project of their own.
IGNORED_DIR_NAMES = frozenset({"obj", "bin", "node_modules", ".git", "dist",
                               "artifacts", "TestResults", "obj-ref"})

SOLUTION_FILE = "SpatialEngine.slnx"

#: How the test projects split across the two CI jobs. The integration lane
#: holds the suites that start Testcontainers — the PostGIS and SQL Server
#: stores and the host that composes them — plus the codec suite, which
#: measures like an integration suite. The unit lane is everything else. The
#: two lanes are disjoint and together cover every test project, so a pull
#: request still gets the full signal, spread over two runners instead of one
#: contended box.
INTEGRATION_FOLDER = "tests/integration/"
INTEGRATION_PROJECTS = frozenset({
    "tests/unit/Spatial.Ingest.Codec.Tests/Spatial.Ingest.Codec.Tests.csproj",
})
LANES = ("unit", "integration")


@dataclass(frozen=True)
class Project:
    """One `.csproj`, its repository-relative path, and its references."""
    path: str
    name: str
    is_test: bool
    references: tuple[str, ...]

    @property
    def directory(self) -> str:
        return str(Path(self.path).parent)


@dataclass(frozen=True)
class Repository:
    """The project's `ProjectReference` graph, keyed by repository path."""
    root: Path
    projects: tuple[Project, ...]

    def by_path(self) -> dict[str, Project]:
        return {p.path: p for p in self.projects}

    def dependents_of(self, changed: set[str]) -> set[str]:
        """Every project that reaches any of `changed` through references.

        A test project references the code it tests, so the tests of a changed
        project are exactly its dependents; a change two hops down is picked up
        because the walk repeats until it stops finding anything new.
        """
        by_path = self.by_path()
        reverse: dict[str, set[str]] = {p.path: set() for p in self.projects}
        for project in self.projects:
            for reference in project.references:
                reverse.setdefault(reference, set()).add(project.path)

        found: set[str] = set(changed)
        frontier = list(changed)
        while frontier:
            current = frontier.pop()
            for dependent in reverse.get(current, ()):
                if dependent not in found:
                    found.add(dependent)
                    frontier.append(dependent)
        return found

    def owning_projects(self, files) -> set[str]:
        """The projects that own `files`, longest directory match wins.

        A file belongs to the project whose directory most specifically
        contains it, so a shared `.props` under the repository root maps to no
        project rather than to every one of them.
        """
        owners: set[str] = set()
        for project in self.projects:
            if any(f == project.path or f.startswith(project.directory + "/")
                   for f in files):
                owners.add(project.path)
        return owners


@dataclass(frozen=True)
class Plan:
    """What the quick lane has to do for one branch."""
    base: str
    changed_files: tuple[str, ...]
    format_projects: tuple[str, ...]
    test_projects: tuple[str, ...]
    run_python_tooling: bool
    #: True when the change set could not be determined, so the caller must run
    #: the full gate rather than a guess.
    exhaustive: bool


def _run_git(root: Path, *args: str) -> tuple[str, bool]:
    """`git <args>` in `root`; returns (output, ok). Never raises."""
    try:
        result = subprocess.run(["git", *args], cwd=root, check=True,
                                capture_output=True, text=True)
    except (subprocess.CalledProcessError, FileNotFoundError):
        return "", False
    return result.stdout, True


def load_repository(root: Path) -> Repository:
    """Read the solution's project list and each project's references."""
    project_files: list[Path] = []
    solution = root / SOLUTION_FILE
    if solution.exists():
        # The .slnx is the project's own list of what the gate builds; reading
        # it beats globbing, which would pick up obj/ and fixtures.
        for line in solution.read_text(encoding="utf-8").splitlines():
            marker = '<Project Path="'
            start = line.find(marker)
            if start < 0:
                continue
            value = line[start + len(marker):]
            project_files.append(root / value[:value.index('"')])
    else:
        project_files = [p for p in root.rglob("*.csproj")
                         if not IGNORED_DIR_NAMES & set(p.parts)]

    projects: list[Project] = []
    for project_file in sorted(project_files):
        if not project_file.exists():
            continue
        tree = ET.parse(project_file)
        is_test = tree.find(".//IsTestProject") is not None
        references = []
        for node in tree.iter("ProjectReference"):
            include = node.get("Include")
            if not include:
                continue
            target = (project_file.parent / include).resolve()
            references.append(_relative(target, root))
        projects.append(Project(
            path=_relative(project_file, root),
            name=project_file.stem,
            is_test=is_test,
            references=tuple(sorted(references)),
        ))
    return Repository(root=root, projects=tuple(projects))


def _relative(path: Path, root: Path) -> str:
    """`path` as a repository-relative POSIX string, resolvable if it can be."""
    try:
        return path.resolve().relative_to(root.resolve()).as_posix()
    except ValueError:
        return path.as_posix()


def changed_files(root: Path, base: str) -> tuple[frozenset[str], bool]:
    """Files changed since `base`, plus the uncommitted ones.

    Returns (files, ok). `ok` is False when git could not answer — an unborn
    `origin/main`, a base that does not resolve, a git that is not there — and
    the caller then runs the full gate rather than testing nothing.
    """
    files: set[str] = set()
    committed, ok = _run_git(root, "diff", "--name-only", f"{base}...HEAD")
    if not ok:
        return frozenset(), False
    files.update(line.strip() for line in committed.splitlines() if line.strip())

    status, ok = _run_git(root, "status", "--porcelain", "--untracked-files=all")
    if not ok:
        return frozenset(), False
    for line in status.splitlines():
        if len(line) < 4:
            continue
        path = line[3:].strip().strip('"')
        # `origin/main -> branch` and `old -> new` forms; the new path is last.
        if " -> " in path:
            path = path.split(" -> ", 1)[1]
        files.add(path)
    return frozenset(files), True


def _is_solution_wide(path: str) -> bool:
    """True for a changed file that no single project owns.

    A root-level `Directory.Packages.props` or `.editorconfig` reaches every
    project in the solution, so scoping to the projects that own the changed
    files would under-run the gate on a branch that changed the build for
    everything. `tests/.../Directory.Build.props` is a different file and stays
    scoped, which is why this is about the root, not the suffix alone.
    """
    return "/" not in path and (
        Path(path).name in SOLUTION_WIDE_FILES
        or Path(path).suffix in (".props", ".targets", ".config", ".editorconfig"))


def plan_quick(repository: Repository, base: str) -> Plan:
    """The quick lane for `base`: format what changed, test what it reaches."""
    files, ok = changed_files(repository.root, base)
    if not ok:
        return Plan(base=base, changed_files=(), format_projects=(),
                    test_projects=(), run_python_tooling=True, exhaustive=True)

    if any(_is_solution_wide(f) for f in files):
        return Plan(base=base, changed_files=tuple(sorted(files)),
                    format_projects=(), test_projects=(),
                    run_python_tooling=True, exhaustive=True)

    formattable = {f for f in files
                   if Path(f).suffix in FORMATTABLE_SUFFIXES}
    format_projects = sorted(repository.owning_projects(formattable))
    changed_projects = repository.owning_projects(formattable)

    reachable = repository.dependents_of(changed_projects)
    test_projects = {p.path for p in repository.projects
                     if p.is_test and p.path in reachable}
    test_projects.add(ARCHITECTURE_PROJECT)
    test_projects = {p for p in test_projects
                     if p in repository.by_path() or p == ARCHITECTURE_PROJECT}

    return Plan(
        base=base,
        changed_files=tuple(sorted(files)),
        format_projects=tuple(format_projects),
        test_projects=tuple(sorted(test_projects)),
        run_python_tooling=any(f.startswith(TOOLING_PREFIX) for f in files),
        exhaustive=False,
    )


def lane_projects(repository: Repository, lane: str) -> tuple[str, ...]:
    """The test projects one CI lane runs.

    The two lanes are disjoint and together are every test project in the
    solution, so splitting the run across jobs loses no signal.
    """
    if lane not in LANES:
        raise ValueError(f"unknown lane {lane!r}; expected one of {LANES}")
    test_projects = sorted(p.path for p in repository.projects if p.is_test)
    if lane == "integration":
        return tuple(p for p in test_projects
                     if p.startswith(INTEGRATION_FOLDER) or p in INTEGRATION_PROJECTS)
    return tuple(p for p in test_projects
                 if not p.startswith(INTEGRATION_FOLDER) and p not in INTEGRATION_PROJECTS)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parent.parent,
                        help="repository root (default: the checkout holding this script)")
    parser.add_argument("--base", default="origin/main",
                        help="the ref the quick lane diffs against (default: origin/main)")
    parser.add_argument("--list", choices=["format", "tests", "tooling", "plan", "lane"],
                        required=True, help="what to print")
    parser.add_argument("--lane", choices=LANES,
                        help="which CI lane to list (with '--list lane')")
    parser.add_argument("--machine", action="store_true",
                        help="print the plan as `kind:value` lines for eng/verify.sh")
    args = parser.parse_args(argv)

    if args.list == "lane" and not args.lane:
        parser.error("--list lane needs '--lane unit|integration'")

    repository = load_repository(args.repo)

    if args.list == "lane":
        for path in lane_projects(repository, args.lane):
            print(path)
        return 0

    plan = plan_quick(repository, args.base)

    if args.machine:
        if args.list != "plan":
            parser.error("--machine applies to '--list plan'")
        # eng/verify.sh reads these keys; an unreadable change set is reported
        # as `exhaustive` so the caller runs the full gate.
        print(f"exhaustive:{1 if plan.exhaustive else 0}")
        for path in plan.format_projects:
            print(f"format:{path}")
        for path in plan.test_projects:
            print(f"tests:{path}")
        print(f"tooling:{1 if plan.run_python_tooling else 0}")
        return 0

    if args.list == "format":
        for path in plan.format_projects:
            print(path)
    elif args.list == "tests":
        for path in plan.test_projects:
            print(path)
    elif args.list == "tooling":
        print("1" if plan.run_python_tooling else "0")
    else:
        if plan.exhaustive:
            print(f"change set unknown against {plan.base}: run the full gate")
        else:
            print(f"base:            {plan.base}")
            print(f"changed files:   {len(plan.changed_files)}")
            for path in plan.format_projects:
                print(f"format:          {path}")
            for path in plan.test_projects:
                print(f"test:            {path}")
            print(f"python tooling:  {'yes' if plan.run_python_tooling else 'no'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
