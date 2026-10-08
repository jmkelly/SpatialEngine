#!/usr/bin/env python3
"""Fail when the repository root carries agent-context sediment (ADR-0148).

    python3 tools/doc_surface.py                    # this checkout
    python3 tools/doc_surface.py --root /some/where # another tree (tests)

The root of this repository is the first thing every agent's file listing has to
reason past, and for a month and a half two of the entries were answers to
questions that go stale between sessions:

*   **`HANDOFF.md`**, 142 lines, last touched 2026-09-15, opened with "the
    GeoServices REST track is the active work" — already false against
    `bd ready`, and confident rather than hedged, which is worse than absent.
    In-flight state is the bead queue's: it is versioned, it is queryable, and
    `bd` is where every workflow already starts;
*   **`CHANGELOG.md`**, 1384 lines at the root, of which 1038 were an
    `## [Unreleased]` section hand-merged from merge to merge by a rule no gate
    touched (a check and a generator now read them: `tools/changelog.py`,
    ADR-0173)
    touched. A release artefact is not context: an agent making a change wants
    `git log` over the path it is touching, and the release checklist is the
    only thing that needs the whole history. It now lives at
    `docs/CHANGELOG.md` and `RELEASING.md` names that path.

Deleting the two files is the cheap half and it does not hold, because nothing
stopped them landing in the first place. This is the three rules that hold:

1.  **No in-flight document at the root.** The names below are the ones that
    answer "what is happening now". A durable document at the root is fine and
    is named rather than inferred: `AGENTS.md`, `README.md` and `RELEASING.md`
    do not go stale between sessions. The rule is
    about the root only — a task's own notes under `research/` is a document
    with a pointer at it, which is the shape the corpus is supposed to have.
2.  **One changelog, at `docs/CHANGELOG.md`.** A second `CHANGELOG.md` beside
    the one the release checklist names is two answers to "what shipped", and
    the release moves one of them. The walk that finds one prunes `IGNORED_DIRS`
    (`node_modules/`, `dist/`, `bin/`, `obj/`, `.git/`) rather than filtering
    their hits out, so the verdict is about what the repository *ships* and does
    not turn red on a checkout that has run `npm install`.
3.  **`<Version>` and the changelog agree.** `RELEASING.md` steps 2 and 3 are
    one edit: a `## [x.y.z]` heading above the product version is a release
    whose version was never bumped, so the tag and the built assembly disagree.
    `## [Unreleased]` is not a release and is not compared — and there is no
    `## [Unreleased]` section any more: `tools/changelog.py --check` is the
    check on the hand-merge, and it runs beside this one on every lane
    (ADR-0173). Versions are
    compared as numbers, so `0.10.0` is above `0.9.0`.

**This is a check the lanes call directly**, next to
`tools/trailing_whitespace.py` and `tools/conflict_markers.py`, and not a
`tools/test_*.py` the tooling suite happens to discover: both `eng/verify.sh`
and `tools/verify_scope.py` run the tooling suite only when the change set
touches `tools/**`, and reintroducing a root session note is a docs or `src`
change — the change set ADR-0146's marker arrived on.
`tools/test_doc_surface.py` keeps the unit tests over the three rules and pins
the wiring.

It reads the root directory, two named files and nothing else, so it costs
milliseconds, needs no .NET SDK, runs in a checkout with no toolchain, changes
nothing and has no `--fix`.
"""
from __future__ import annotations

import argparse
import os
import re
import sys
from pathlib import Path

#: The one changelog, and the path `RELEASING.md` names. Under `docs/` because
#: it is a release artefact rather than agent context (ADR-0148).
CHANGELOG_PATH = "docs/CHANGELOG.md"

#: The version the assembly is stamped from, single-sourced in
#: `Directory.Build.props` and mirrored in the changelog.
BUILD_PROPS = "Directory.Build.props"

#: Documents at the root whose whole job is to say what is happening now. The
#: answer to that question is `bd ready`, which is versioned and queryable;
#: these are the three-sources-of-truth files that drifted from it. Matched on
#: the stem, case-insensitively, so `HANDOFF`, `handoff.md` and `Handoff.MD`
#: are one name rather than three.
SESSION_DOC_STEMS = (
    "handoff",
    "notes",
    "todo",
    "status",
    "progress",
    "session",
    "next",
    "scratch",
)

#: The root documents that answer a question which does not go stale between
#: sessions. Named rather than inferred, because a shape ("a `.md` file at the
#: root") would be a rule about every file an agent adds.
DURABLE_ROOT_DOCS = frozenset({
    "agents.md", "readme.md", "releasing.md", "changelog.md",
    "license",
})

#: Directory names the changelog walk prunes rather than filtering afterwards.
#: `Path.rglob` has no skip set, so it descends into every one of them, and the
#: directories it descends into are exactly the ones the repository does not
#: ship: `node_modules/` is `.gitignore`d wholesale and carries a `CHANGELOG.md`
#: per package, and `dist/`, `bin/` and `obj/` are build output. Pruning is
#: also the cheaper way to not look — a checkout that has run `npm install` has
#: tens of thousands of files under `node_modules/`, and the walk was paying to
#: enumerate them on every lane. Pruned by name, not by reading `.gitignore`:
#: the gate is about what the repository ships, and a hand-listed set is a set
#: someone can check by reading it.
IGNORED_DIRS = frozenset({
    ".git", "node_modules", "dist", "bin", "obj",
})

VERSION_PROPERTY = re.compile(r"<Version>\s*([^<]+?)\s*</Version>")
VERSION = re.compile(r"(\d+)\.(\d+)\.(\d+)")
RELEASE_HEADING = re.compile(r"^##\s+\[(\d+(?:\.\d+)*)\](?:\s*-\s*|$)", re.M)


def read(path: Path) -> str:
    """The file's text, or empty for one that is absent or unreadable."""
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError):
        return ""


def product_version(root: Path) -> tuple[int, ...] | None:
    """The `<Version>` in `Directory.Build.props` as a tuple of numbers.

    `None` when the property is missing or is not three numbers: silence would
    be worse, because every comparison would pass and the check would be a
    no-op on the release it exists to catch.
    """
    match = VERSION_PROPERTY.search(read(root / BUILD_PROPS))
    if match is None:
        return None
    parts = VERSION.fullmatch(match.group(1))
    return tuple(int(number) for number in parts.groups()) if parts else None


def released_versions(root: Path) -> list[str]:
    """The `## [x.y.z] - date` headings in the changelog, in file order."""
    return RELEASE_HEADING.findall(read(root / CHANGELOG_PATH))


def changelogs(root: Path) -> list[Path]:
    """Every `CHANGELOG.md` under `root` that is not inside a pruned directory."""
    found: list[Path] = []
    for directory, subdirectories, files in os.walk(root):
        subdirectories[:] = sorted(
            name for name in subdirectories if name not in IGNORED_DIRS
        )
        if "CHANGELOG.md" in files:
            found.append(Path(directory) / "CHANGELOG.md")
    return sorted(found)


def findings(root: Path) -> list[str]:
    """Every way the repository root can carry agent-context sediment."""
    reported: list[str] = []

    # 1. in-flight documents at the root
    for path in sorted(root.iterdir() if root.is_dir() else []):
        if not path.is_file():
            continue
        stem = path.stem if path.suffix else path.name
        if stem.lower() in SESSION_DOC_STEMS and path.name.lower() not in DURABLE_ROOT_DOCS:
            reported.append(
                f"{path.relative_to(root)}: a document that answers 'what is "
                "happening now' does not belong at the repository root; in-flight "
                "state is the bead queue (`bd ready`), which is versioned and "
                "queryable (ADR-0148)"
            )

    # 2. one changelog, at the documented path
    changelog = root / CHANGELOG_PATH
    if not changelog.is_file():
        reported.append(
            f"{CHANGELOG_PATH}: no such file; the release artefact is a release "
            "artefact, and `RELEASING.md` names this path (ADR-0148)"
        )
    for path in changelogs(root):
        if path.is_file() and path.relative_to(root).as_posix() != CHANGELOG_PATH:
            reported.append(
                f"{path.relative_to(root)}: a second changelog beside "
                f"{CHANGELOG_PATH}, which is the one `RELEASING.md` names; two "
                "answers to 'what shipped' is one too many (ADR-0148)"
            )

    # 3. <Version> and the changelog agree
    version = product_version(root)
    if version is None:
        reported.append(
            f"{BUILD_PROPS}: no <Version>x.y.z</Version> to compare the changelog "
            "against; the release-version check cannot run (ADR-0148)"
        )
    else:
        for released in released_versions(root):
            parts = VERSION.fullmatch(released)
            if parts is None:
                continue
            if tuple(int(number) for number in parts.groups()) > version:
                reported.append(
                    f"{CHANGELOG_PATH}: released version {released} is above "
                    f"<Version>{'.'.join(str(number) for number in version)}"
                    f"</Version> in {BUILD_PROPS}; `RELEASING.md` steps 2 and 3 "
                    "are one edit (ADR-0148)"
                )

    return reported


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=".", help="repository root (default: .)")
    arguments = parser.parse_args(argv)
    reported = findings(Path(arguments.root).resolve())
    if reported:
        print(f"doc-surface: {len(reported)} finding(s):", file=sys.stderr)
        for finding in reported:
            print(f"  {finding}", file=sys.stderr)
        return 1
    print("doc-surface: the root carries no agent-context sediment")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
