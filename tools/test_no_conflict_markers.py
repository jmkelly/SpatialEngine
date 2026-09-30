#!/usr/bin/env python3
"""No tracked file in the repository may carry an unresolved merge-conflict marker.

Run: python3 -m unittest tools/test_no_conflict_markers.py

A merge that resolves with a marker left behind is not a merge failure the
tooling notices: git reports a clean tree, the build compiles (a marker inside
a markdown file is prose, not syntax), and the conflict body ships as content.
That is how a `<<<<<<< HEAD` line reached `CHANGELOG.md` on main and rendered
as changelog text, and no gate saw it, because nothing read the file.

The merge that shipped it was checked afterwards (SpatialEngine-aot), because a
marker with no partner is either a botched resolution or a forgotten one and
the two are told apart by the merge, not by the file. `git log -S` puts the
line in `2fb24b6`, the SpatialEngine-u2x.37 commit on its own branch, so it was
written there and not by the resolution. The SpatialEngine-u2x.37 merge
`9429422` then carried it through untouched: `git diff c7b8158 9429422 --
CHANGELOG.md` is empty, so the merge's changelog *is* the branch tip's, and
`git diff 72d8f9e 9429422 -- CHANGELOG.md` is additions only — the ADR-0119
entry the first parent already carried is context in it, and the ADR-0120 entry
is the branch's. Nothing was dropped, and the reason is in the parentage:
`git merge-base 72d8f9e c7b8158` is `72d8f9e` itself, the merge's own first
parent, so the branch was cut from the mainline the merge landed on and the
two sides had no divergent changelog to lose. (ADR-0121's entry arrived later
and separately, with SpatialEngine-u2x.43.)

That is safe *only* because of that parentage, and it is a case this gate
cannot generalise: a merge whose sides had both edited the file is a resolution
whose dropped hunk leaves no marker behind, and nothing here can see it.

So this is the gate's answer, and it is deliberately blunt: every tracked file
is scanned, not just the files a merge touched. The markers are assembled from
character runs rather than written literally, so that this file — which is
itself tracked, and which must describe the markers it forbids — does not trip
its own rule.
"""
import subprocess
import tempfile
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


#: The line as `CHANGELOG.md` carried it at `9429422`, with no `=======` or
#: `>>>>>>>` partner anywhere in the file: the one-character-class orphan the
#: SpatialEngine-u2x.37 merge committed. Assembled from the same run, so that
#: this file does not contain the line it hunts for.
ORPHAN = SIDE[0] * 7 + " HEAD\n### Changed\n\n- **A layer's extent is reduced at the store**\n"

#: A whole unresolved block, which is what a conflict looks like when the
#: resolution is forgotten rather than botched.
BLOCK = (
    SIDE[0] * 7 + " HEAD\n"
    "ours\n"
    + SEPARATOR + "\n"
    "theirs\n"
    + SIDE[1] * 7 + " branch\n"
)


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


def findings_in_tracked(root):
    """The `name:line: text` findings over every tracked file under `root`."""
    findings = []
    for name in tracked_files(root):
        path = root / name
        if not path.is_file():
            continue
        findings.extend(f"{name}:{number}: {line}" for number, line in markers_in(path))
    return findings


class MarkerTests(unittest.TestCase):
    """What the match finds, and the three edges it deliberately rounds off.

    The first case is the historical one, byte for byte: an orphan side line
    with no partner, which is the shape a *forgotten* resolution leaves and the
    shape that reached `CHANGELOG.md`.
    """

    def read(self, text: str, encoding: str = "utf-8"):
        """The findings over one temporary file holding `text`."""
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "CHANGELOG.md"
            path.write_text(text, encoding=encoding)
            return markers_in(path)

    def test_the_orphan_marker_from_9429422_is_found(self):
        found = self.read(ORPHAN)
        self.assertEqual(found, [(1, SIDE[0] * 7 + " HEAD")],
                         "a side line with no partner is still a marker")

    def test_every_line_of_an_unresolved_block_is_found(self):
        found = self.read(BLOCK)
        self.assertEqual([number for number, _ in found], [1, 3, 5])

    def test_the_repository_and_the_checker_ship_no_marker(self):
        # The two findings that are the point of the file: the checker is
        # written out of character runs so it does not trip its own rule, and
        # the repository is clean.
        self.assertEqual(findings_in_tracked(REPO_ROOT), [])

    def test_an_underline_that_is_not_seven_characters_is_not_a_separator(self):
        # reStructuredText underlines a heading with `=` and markdown setext
        # with `-`; neither is a merge, and only the exact separator run is
        # matched, so a longer run is not read as one.
        self.assertEqual(self.read("A heading\n========\n\nand\n=====\n"), [])

    def test_a_seven_character_underline_is_reported(self):
        # The residue: a heading underlined with exactly seven `=` is
        # indistinguishable from a separator by line content alone, and this
        # repository has none. It is reported rather than guessed at, because
        # guessing is what let the orphan through.
        self.assertEqual([number for number, _ in self.read("Title\n" + SEPARATOR + "\n")], [2])

    def test_an_indented_marker_is_not_a_marker(self):
        # git writes both sides at column 0, and an indented `=======` under a
        # line of prose is a rule in a table or a comment banner, so leading
        # whitespace is not stripped before matching.
        self.assertEqual(self.read("    " + SIDE[0] * 7 + " HEAD\n"), [])

    def test_undecodable_bytes_are_not_read_as_text(self):
        # A PNG's bytes are not a merge conflict however they fall; this is a
        # text-hygiene sweep, not a validity check.
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "logo.png"
            path.write_bytes(b"\x89PNG\r\n\x1a\n\xff\xfe" + SIDE[0].encode() * 7)
            self.assertEqual(markers_in(path), [])


class ConflictMarkerTests(unittest.TestCase):
    def test_no_tracked_file_carries_a_conflict_marker(self):
        findings = findings_in_tracked(REPO_ROOT)
        self.assertEqual(
            findings,
            [],
            "unresolved merge-conflict markers in tracked files:\n  "
            + "\n  ".join(findings),
        )


if __name__ == "__main__":
    unittest.main()
