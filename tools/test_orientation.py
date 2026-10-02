#!/usr/bin/env python3
"""Tests for tools/orientation.py — the coverage report over Orientation blocks.

Run: python3 -m unittest tools/test_orientation.py

`SpatialEngine-rzq` (epic `SpatialEngine-imz`, gap G9) added one class of fact
the epic's other eight children cannot produce: **orientation** — where the
first hour of a bead goes, written in the imperative, one line per bead, under
an `## Orientation` heading in the digest that owns the area. The evidence for
that class (arXiv 2606.20512, 2607.27250) is coverage, not precision: refined
guidance produced evaluable patches for +14.5pp more instances while per-patch
precision stayed flat. A guidance line helps an agent *reach the correct file*;
it does not make the agent write better code once there.

The bead is explicit that this is **not a gate and not a doc-freshness check**,
because it cannot be mechanised — it is the one place in the doc surface where
prose is the right instrument. So the tool here reports and lists; it is wired
into nothing, and `eng/verify.sh` does not call it. What *is* mechanisable, and
what these tests pin, is the shape the prose has to keep:

*   a block is delimited by `<!-- orientation:begin -->` /
    `<!-- orientation:end -->`, so a generator (the G4 child bead
    `SpatialEngine-imz.5`, which must derive its nested `AGENTS.md` rather than
    hand-write them) can read exactly the block and nothing around it;
*   every line names the bead it came from, so a line is traceable to
    `git log` and a reader can go and read the close;
*   the corpus carries at least the twenty closed beads the acceptance criteria
    name, one line each, and `apps/workbench-web` — the largest TypeScript
    surface, which had no scoped agent document at all — has a block of its own.

The traceability half needs history, and a CI checkout is shallow
(`actions/checkout@v7` at its default depth), so it is asserted only when the
history is deep enough to judge and skipped otherwise — the same
report-don't-fail shape `tools/beads_gate.py` uses for the one input CI cannot
have.
"""
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPT = REPO_ROOT / "tools" / "orientation.py"
DISTILLED = REPO_ROOT / "architecture" / "distilled"
WORKBENCH_AGENTS = REPO_ROOT / "apps" / "workbench-web" / "AGENTS.md"

#: The acceptance criterion, as a number: twenty closed beads have produced an
#: Orientation line in the distilled docs.
REQUIRED_BEADS = 20

sys.path.insert(0, str(REPO_ROOT / "tools"))
import orientation  # noqa: E402


class ParseTests(unittest.TestCase):
    """The block shape a generator reads."""

    def test_a_block_is_its_lines_and_nothing_around_them(self):
        text = "\n".join([
            "# Digest", "", "prose before",
            orientation.BEGIN, "## Orientation", "",
            "- Start at the reader, not the provider. (SpatialEngine-u2x.17)",
            orientation.END, "", "prose after", ""])
        lines = orientation.parse_block(text, "digest.md")
        self.assertEqual(len(lines), 1)
        self.assertEqual(lines[0].bead, "SpatialEngine-u2x.17")
        self.assertIn("Start at the reader", lines[0].text)

    def test_a_line_without_a_bead_parses_but_is_a_finding(self):
        # Not an error: the shape is readable, the discipline is not met. A
        # line nobody can trace to a close is the one line that will rot.
        lines = orientation.parse_block(
            orientation.BEGIN + "\n- Something true and untethered.\n" + orientation.END,
            "digest.md")
        self.assertEqual(lines[0].bead, None)
        self.assertEqual([l.bead for l in orientation.untraced(lines)], [None])

    def test_the_bead_is_read_in_either_spelling(self):
        for line in ("- Do the thing. (SpatialEngine-u2x.8)",
                     "- Do the thing. — SpatialEngine-u2x.8",
                     "- Do the thing. `SpatialEngine-u2x.8`"):
            with self.subTest(line=line):
                parsed = orientation.parse_block(
                    orientation.BEGIN + "\n" + line + "\n" + orientation.END, "d.md")
                self.assertEqual(parsed[0].bead, "SpatialEngine-u2x.8")

    def test_an_unbalanced_block_is_an_error(self):
        for text in (orientation.BEGIN + "\n- A line. (SpatialEngine-u2x.8)\n",
                     "- A line. (SpatialEngine-u2x.8)\n" + orientation.END,
                     orientation.BEGIN + "\n- A. (SpatialEngine-u2x.8)\n" + orientation.END
                     + orientation.BEGIN + "\n- B. (SpatialEngine-u2x.3)\n" + orientation.END):
            with self.subTest(text=text):
                with self.assertRaises(orientation.OrientationError):
                    orientation.parse_block(text, "d.md")

    def test_a_heading_is_not_an_orientation_line(self):
        lines = orientation.parse_block(
            orientation.BEGIN + "\n## Orientation\n\n- Real. (SpatialEngine-u2x.8)\n"
            + orientation.END, "d.md")
        self.assertEqual([l.bead for l in lines], ["SpatialEngine-u2x.8"])

    def test_the_block_preamble_is_not_a_line(self):
        # The block carries a sentence saying what it is; that sentence is
        # structure the reader sees, not a fact traceable to a bead.
        lines = orientation.parse_block(
            orientation.BEGIN + "\n## Orientation\n\nOne line per closed bead.\n\n"
            "- Real. (SpatialEngine-u2x.8)\n" + orientation.END, "d.md")
        self.assertEqual([l.text for l in lines], ["- Real. (SpatialEngine-u2x.8)"])

    def test_a_wrapped_fact_is_one_line(self):
        # These are written at the prose width the corpus uses, and a wrapped
        # fact is still one fact for one bead.
        lines = orientation.parse_block(
            orientation.BEGIN + "\n- A fact that\n  wraps. (SpatialEngine-u2x.8)\n"
            + orientation.END, "d.md")
        self.assertEqual(len(lines), 1)
        self.assertEqual(lines[0].text, "- A fact that wraps. (SpatialEngine-u2x.8)")
        self.assertEqual(lines[0].bead, "SpatialEngine-u2x.8")


class CoverageTests(unittest.TestCase):
    """The report the bead asks for: coverage, not a verdict on quality."""

    def test_coverage_counts_lines_files_and_distinct_beads(self):
        blocks = [
            orientation.Block("a.md", [orientation.Line("a.md", "- One. (SpatialEngine-u2x.8)",
                                                          "SpatialEngine-u2x.8")]),
            orientation.Block("b.md", [orientation.Line("b.md", "- Two. (SpatialEngine-u2x.3)",
                                                         "SpatialEngine-u2x.3")]),
        ]
        report = orientation.coverage(blocks, {"SpatialEngine-u2x.8"})
        self.assertEqual(report.lines, 2)
        self.assertEqual(len(report.files), 2)
        self.assertEqual(report.beads, {"SpatialEngine-u2x.8", "SpatialEngine-u2x.3"})
        self.assertEqual(report.untraceable, {"SpatialEngine-u2x.3"})

    def test_the_report_renders_without_a_queue_and_without_history(self):
        blocks = [orientation.Block("a.md", [orientation.Line("a.md", "- One. (SpatialEngine-u2x.8)",
                                                              "SpatialEngine-u2x.8")])]
        text = orientation.render(orientation.coverage(blocks, set()))
        self.assertIn("a.md", text)
        self.assertIn("SpatialEngine-u2x.8", text)


class ListTests(unittest.TestCase):
    """`--list` is how the G4 generator reads a package instead of writing one."""

    def test_list_prints_the_block_body(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "architecture" / "distilled").mkdir(parents=True)
            digest = root / "architecture" / "distilled" / "core.md"
            digest.write_text("\n".join([
                "# Core", "", orientation.BEGIN, "## Orientation", "",
                "- A line. (SpatialEngine-a74.2)", orientation.END, ""]))
            out = orientation.main(["--root", str(root), "--list",
                                    "architecture/distilled/core.md"])
            self.assertEqual(out, 0)

    def test_listing_a_path_with_no_block_is_an_error(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "AGENTS.md").write_text("# Nothing here\n")
            with self.assertRaises(orientation.OrientationError):
                orientation.main(["--root", str(root), "--list", "AGENTS.md"])


class RepositoryTests(unittest.TestCase):
    """The corpus the acceptance criteria name."""

    @classmethod
    def setUpClass(cls):
        cls.blocks = orientation.discover(REPO_ROOT)
        cls.history = orientation.history_beads(REPO_ROOT)
        cls.report = orientation.coverage(cls.blocks, cls.history)

    def test_every_distilled_digest_has_an_orientation_block(self):
        paths = {Path(b.path).name for b in self.blocks
                 if b.path.startswith("architecture/distilled/")}
        expected = {p.name for p in DISTILLED.glob("*.md")}
        self.assertEqual(expected - paths, set(), "a digest with no Orientation block")

    def test_the_workbench_has_its_own(self):
        # The largest TypeScript surface had no scoped agent document at all,
        # which is why the acceptance criteria name it separately.
        self.assertIn("apps/workbench-web/AGENTS.md", {b.path for b in self.blocks})

    def test_twenty_closed_beads_have_produced_a_line(self):
        distilled = [b for b in self.blocks if b.path.startswith("architecture/distilled/")]
        beads = orientation.coverage(distilled, self.history).beads
        self.assertGreaterEqual(
            len(beads), REQUIRED_BEADS,
            f"orientation covers {len(beads)} beads, not {REQUIRED_BEADS}")

    def test_every_line_names_its_bead(self):
        self.assertEqual([(l.path, l.text) for l in orientation.untraced(
            [l for b in self.blocks for l in b.lines])], [])

    def test_every_line_is_one_line_and_imperative_ish(self):
        # The failure mode the bead names is volume: a block that reads like a
        # wiki has crossed into skill leakage, and the fix is a skill, not more
        # prose. A line that is a paragraph is two facts wearing one bead id.
        for block in self.blocks:
            for line in block.lines:
                self.assertNotIn("\n", line.text)
                self.assertLessEqual(len(line.text), 300, f"{block.path}: {line.text}")

    @unittest.skipUnless(
        not orientation.history_is_shallow(REPO_ROOT),
        "history too shallow to trace a bead id (CI checks out depth 1)")
    def test_every_cited_bead_is_traceable_in_git_history(self):
        self.assertEqual(self.report.untraceable, set(),
                         "an Orientation line cites a bead no commit carries a "
                         "Task: trailer for")


class WiringTests(unittest.TestCase):
    """The bead's own constraint: this is not a gate."""

    def test_no_verification_lane_calls_the_tool(self):
        verify = (REPO_ROOT / "eng" / "verify.sh").read_text(encoding="utf-8")
        self.assertNotIn("orientation.py", verify)
        self.assertNotIn("orientation.py", (REPO_ROOT / "eng" / "quality-audit.sh")
                         .read_text(encoding="utf-8"))

    def test_the_close_checklist_asks_the_question(self):
        agents = (REPO_ROOT / "AGENTS.md").read_text(encoding="utf-8")
        self.assertRegex(agents, re.compile(r"first hour", re.IGNORECASE))


if __name__ == "__main__":
    unittest.main()
