#!/usr/bin/env python3
"""No tracked file in the repository may carry an unresolved merge-conflict marker.

Run: python3 -m unittest tools/test_no_conflict_markers.py

A merge that resolves with a marker left behind is not a merge failure the
tooling notices: git reports a clean tree, the build compiles (a marker inside
a markdown file is prose, not syntax), and the conflict body ships as content.
That is how a `<<<<<<< HEAD` line reached `CHANGELOG.md` on main and rendered
as changelog text — it arrived with the SpatialEngine-lyz merge and no gate
saw it, because nothing read the file.

So this is the gate's answer, and it is deliberately blunt: every tracked file
is scanned, not just the files a merge touched. The markers are assembled from
character runs rather than written literally, so that this file — which is
itself tracked, and which must describe the markers it forbids — does not trip
its own rule.
"""
import subprocess
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

#: Built by repetition rather than written out: a literal marker at the start
#: of a line in this source would be the very thing the test hunts for.
#: Git writes the two sides as a seven-character run followed by a space and
#: the branch name, so those are matched as prefixes; the separator is matched
#: whole, because a longer run of `=` is an underline in reStructuredText and
#: underlining something is not a merge conflict.
SIDE = ("<", ">")
SEPARATOR = "=" * 7


def tracked_files(root):
    """Every file git tracks, relative to the repository root.

    Tracked rather than on-disk: the question is what the repository ships, so
    a build artefact or an editor's swap file is not a finding. Untracked files
    are also what `git diff --name-only` in the scoping lane never sees, and
    the marker that reached main was committed, not left loose.
    """
    listing = subprocess.run(
        ["git", "-C", str(root), "ls-files", "-z"],
        check=True,
        capture_output=True,
    )
    return [name for name in listing.stdout.decode("utf-8").split("\0") if name]


def markers_in(path):
    """The (line number, line) pairs in `path` that look like conflict markers.

    Undecodable bytes are read leniently: this is a text-hygiene sweep, not a
    validity check, and a PNG's bytes are not a merge conflict however they fall.
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


class ConflictMarkerTests(unittest.TestCase):
    def test_no_tracked_file_carries_a_conflict_marker(self):
        findings = []
        for name in tracked_files(REPO_ROOT):
            path = REPO_ROOT / name
            if not path.is_file():
                continue
            for number, line in markers_in(path):
                findings.append(f"{name}:{number}: {line}")
        self.assertEqual(
            findings,
            [],
            "unresolved merge-conflict markers in tracked files:\n  "
            + "\n  ".join(findings),
        )


if __name__ == "__main__":
    unittest.main()
