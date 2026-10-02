#!/usr/bin/env python3
"""Fail on an unresolved merge-conflict marker in a tracked file.

    python3 tools/conflict_markers.py                    # every file git tracks
    python3 tools/conflict_markers.py --repo /some/where # another checkout

A merge that resolves with a marker left behind is not a merge failure the
tooling notices: git reports a clean tree, the build compiles (a marker inside
a markdown file is prose, not syntax), and the conflict body ships as content.
That is how a `<<<<<<< HEAD` line reached `CHANGELOG.md` on main and rendered
as changelog text (SpatialEngine-u2x.37's `9429422`, judged in SpatialEngine-aot),
and no gate saw it, because nothing read the file.

It was invisible to the merge gate for a second reason, which is what this
script exists for. The rule was a `tools/test_*.py`, and both `eng/verify.sh`
and `tools/verify_scope.py` run the tooling suite only when the change set
touches `tools/**` (`run_python_tooling`, tools/verify_scope.py:355). So the
fast lane — the merge gate since ADR-0134 — skipped the one check that would
have caught it, on a `CHANGELOG.md` merge whose change set was docs and `src`
with no `tools/` file in it. CI's `verify` job runs the tooling tests
unconditionally (.github/workflows/ci.yml:88), so the marker was caught after
the merge to `main`: the ADR-0143 hole the trailing-whitespace step closed, in
the same shape and a week later.

**This is a check the lanes call directly**, next to
`tools/trailing_whitespace.py`, and not a test the tooling lane happens to
discover: a rule that only runs when `tools/**` changed is not a merge gate.
`tools/test_no_conflict_markers.py` keeps the unit tests over the matching and
pins the wiring.

It is deliberately blunt: every tracked file is scanned, not only the files a
merge touched. It changes nothing, has no `--fix`, and needs no .NET SDK, so it
also runs in a checkout with no toolchain. Files come from
`git ls-files --cached`: the question is what the repository ships, so a build
artefact or an editor's swap file is not a finding.

The markers are assembled from character runs rather than written literally, so
that this file — which is itself tracked, and which must describe the markers
it forbids — does not trip its own rule. Git writes the two sides as a
seven-character run followed by a space and the branch name, so those are
matched as prefixes; the separator is matched whole, because a longer run of
`=` is an underline in reStructuredText and underlining something is not a
merge conflict.
"""
from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

DEFAULT_REPO = Path(__file__).resolve().parent.parent

#: Built by repetition rather than written out: a literal marker at the start
#: of a line in this source would be the very thing the check hunts for.
SIDE = ("<", ">")
SEPARATOR = "=" * 7


def tracked_files(root: Path) -> list[str]:
    """Every file git tracks under `root`, relative to the repository root.

    Tracked rather than on-disk: the question is what the repository ships, so
    a build artefact or an editor's swap file is not a finding. Untracked files
    are also what `git diff --name-only` in the scoping lane never sees, and
    the marker that reached main was committed, not left loose.
    """
    listing = subprocess.run(
        ["git", "-C", str(root), "ls-files", "-z"],
        capture_output=True,
    )
    if listing.returncode != 0:
        raise SystemExit(f"git could not list the tracked files under {root}")
    return [name for name in listing.stdout.decode("utf-8", errors="replace").split("\0") if name]


def markers_in(path: Path) -> list[tuple[int, str]]:
    """The (line number, line) pairs in `path` that look like conflict markers.

    Undecodable bytes are read leniently: this is a text-hygiene sweep, not a
    validity check, and a PNG's bytes are not a merge conflict however they fall.
    Leading whitespace is not stripped before matching, because git writes both
    sides at column 0 and an indented `=======` under a line of prose is a rule
    in a table or a comment banner.
    """
    try:
        text = path.read_text(encoding="utf-8")
    except (UnicodeDecodeError, OSError):
        return []
    found = []
    for number, line in enumerate(text.splitlines(), start=1):
        if line == SEPARATOR or any(line.startswith(c * 7 + " ") for c in SIDE):
            found.append((number, line))
    return found


def findings_in_tracked(root: Path) -> list[str]:
    """The `name:line: text` findings over every tracked file under `root`."""
    findings = []
    for name in tracked_files(root):
        path = root / name
        if not path.is_file():
            continue
        findings.extend(f"{name}:{number}: {line}" for number, line in markers_in(path))
    return findings


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Fail on unresolved merge-conflict markers in tracked files.")
    parser.add_argument("--repo", type=Path, default=DEFAULT_REPO,
                        help="repository root (default: the checkout holding this script)")
    args = parser.parse_args(argv)
    findings = findings_in_tracked(args.repo.resolve())
    for finding in findings:
        print(f"conflict-marker: {finding}", file=sys.stdout)
    return 1 if findings else 0


if __name__ == "__main__":
    raise SystemExit(main())
