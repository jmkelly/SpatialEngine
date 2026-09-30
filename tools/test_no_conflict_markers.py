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

That answer was carried by *this* test, and that was the defect: the tooling
suite runs only when the change set touches `tools/**`, so the merge gate never
read the rule on a change that did not — and the marker arrived on exactly such
a change, a `CHANGELOG.md` merge whose change set was docs and `src`. The check
is now `tools/conflict_markers.py`, which every lane calls directly
(ADR-0146); this file keeps the matching's unit tests and the wiring tests that
hold it there.
"""
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

# The matching itself lives in `tools/conflict_markers.py`, which is what the
# lanes call (LaneWiringTests below). One rule, one implementation: a test that
# carries a second copy of it can pass while the check every merge runs fails.
from tools.conflict_markers import (  # noqa: E402  (path is set by the runner)
    SEPARATOR,
    SIDE,
    findings_in_tracked,
    markers_in,
)

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPT = REPO_ROOT / "tools" / "conflict_markers.py"
VERIFY_SH = REPO_ROOT / "eng" / "verify.sh"


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


class LaneWiringTests(unittest.TestCase):
    """The rule has to be read by the lanes that gate a merge.

    This gate shipped as a `tools/test_*.py`, and both `eng/verify.sh` and
    `tools/verify_scope.py` run the tooling suite only when the change set
    touches `tools/**` (`run_python_tooling`, tools/verify_scope.py:355). So it
    did not run on the fast lane for a change that touches no tool — which is
    exactly the kind of change the marker arrived on, a `CHANGELOG.md` merge
    (SpatialEngine-u2x.37's `9429422`, checked in SpatialEngine-aot). The
    detector is `tools/conflict_markers.py` and every lane calls it, the shape
    ADR-0143 gives the trailing-whitespace rule.
    """

    def lanes(self):
        """`eng/verify.sh` split into the text of each lane's block."""
        text = VERIFY_SH.read_text(encoding="utf-8")
        markers = ["# --- the format lane",
                   "# --- the full lane",
                   "# --- the default lane"]
        self.assertTrue(all(m in text for m in markers),
                        "eng/verify.sh no longer carries the lane markers these tests split on")
        blocks = {}
        for index, marker in enumerate(markers):
            end = markers[index + 1] if index + 1 < len(markers) else len(text)
            blocks[marker] = text[text.index(marker):text.index(end) if end != len(text) else len(text)]
        return blocks

    def test_every_verify_lane_runs_the_check(self):
        for marker, block in self.lanes().items():
            self.assertIn("conflict_marker_step", block,
                          f"the lane at {marker!r} does not run the conflict-marker check")

    def test_the_helper_runs_the_check(self):
        text = VERIFY_SH.read_text(encoding="utf-8")
        start = text.index("conflict_marker_step() {")
        body = text[start:text.index("\n}", start)]
        self.assertIn("tools/conflict_markers.py", body)

    def test_the_fast_lane_does_not_gate_it_on_tools_changing(self):
        # The regression itself: the tooling tests are conditional on the change
        # set touching `tools/**`, so a step inside that block would not run on
        # the merge gate for a docs or src change. The call is therefore
        # asserted to stand before the conditional, not inside it.
        text = VERIFY_SH.read_text(encoding="utf-8")
        default = self.lanes()["# --- the default lane"]
        conditional = default.index('if [[ "$RUN_TOOLING" == "1" ]]')
        self.assertIn("conflict_marker_step", default[:conditional],
                      "the conflict-marker check has fallen inside the tools/**-only tooling gate")
        self.assertNotIn("conflict_marker_step", default[conditional:])

    def test_the_repository_is_clean(self):
        # The detector as a command, which is how the lanes call it: a rule
        # only the test exercises is the same invisible hole one level up.
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--repo", str(REPO_ROOT)],
            capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_an_orphan_marker_fails_the_check(self):
        # The finding is the exit code and the `path:line` report, because a
        # lane reads nothing else.
        with tempfile.TemporaryDirectory() as root:
            subprocess.run(["git", "-C", root, "init", "-q"], check=True)
            (Path(root) / "CHANGELOG.md").write_text(ORPHAN, encoding="utf-8")
            subprocess.run(["git", "-C", root, "add", "CHANGELOG.md"], check=True)
            result = subprocess.run(
                [sys.executable, str(SCRIPT), "--repo", root],
                capture_output=True, text=True,
            )
        self.assertEqual(result.returncode, 1)
        self.assertIn("CHANGELOG.md:1", result.stdout)


def test_ci_runs_the_check(self):
        # The CI `verify` job spells its steps out rather than calling the
        # script, so wiring the lanes alone would leave the detector that
        # follows every merge with the same hole.
        self.assertIn("tools/conflict_markers.py",
                      (REPO_ROOT / ".github" / "workflows" / "ci.yml").read_text(encoding="utf-8"))


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
