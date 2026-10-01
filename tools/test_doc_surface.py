#!/usr/bin/env python3
"""The repository root carries no in-flight state, and the changelog is a release artefact.

Run: python3 -m unittest tools/test_doc_surface.py

`HANDOFF.md` and `CHANGELOG.md` sat at the repository root for a month and a
half, and both are answers to a question the bead queue already answers:
`HANDOFF.md` was 142 lines of "what is happening right now" whose opening claim
("the GeoServices REST track is the active work") was already false against
`bd ready`, and `CHANGELOG.md` was 1384 lines of release history — 1038 of them
an `## [Unreleased]` section hand-merged from merge to merge, which no gate
touched. Together with `eng/swarm-runbook.md` and the beads database that is
three sources of truth for in-flight state, and duplication of one meaning in
several places is the cost to remove (writing-for-agents, *Pruning*).

Deleting the two files is the cheap half and it does not hold: nothing stops the
next session note landing in the root, and nothing notices a second changelog
beside the one the release checklist names. So the rule is a **check the lanes
call directly**, next to `tools/trailing_whitespace.py` and
`tools/conflict_markers.py` (ADR-0143, ADR-0146) and not a `tools/test_*.py` the
tooling suite happens to discover: both `eng/verify.sh` and
`tools/verify_scope.py` run the tooling suite only when the change set touches
`tools/**`, and a docs or `src` merge is exactly the change set that would
reintroduce this. ADR-0148.

The check reads the root directory, `docs/CHANGELOG.md` and the `<Version>` in
`Directory.Build.props`. It changes nothing, has no `--fix`, and needs no .NET
SDK, so it runs in a checkout with no toolchain.
"""
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

# The rules live in `tools/doc_surface.py`, which is what the lanes call
# (LaneWiringTests below). One rule, one implementation: a test that carries a
# second copy of it can pass while the check every merge runs fails.
from tools.doc_surface import CHANGELOG_PATH, findings  # noqa: E402  (path is set by the runner)

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPT = REPO_ROOT / "tools" / "doc_surface.py"
VERIFY_SH = REPO_ROOT / "eng" / "verify.sh"
CI_YML = REPO_ROOT / ".github" / "workflows" / "ci.yml"


def make_repo(root: Path, version: str = "0.3.0", changelog: str | None = None,
              changelog_at: str | None = "docs/CHANGELOG.md") -> Path:
    """A throwaway tree shaped like this repository's root."""
    (root / "Directory.Build.props").write_text(
        "<Project>\n  <PropertyGroup>\n"
        f"    <Version>{version}</Version>\n"
        "  </PropertyGroup>\n</Project>\n",
        encoding="utf-8",
    )
    if changelog is None and changelog_at is not None:
        changelog = (
            "# Changelog\n\n## [Unreleased]\n\n- **Something**\n\n"
            f"## [{version}] - 2026-09-15\n\n- **Shipped**\n"
        )
    if changelog is not None:
        target = root / changelog_at
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(changelog, encoding="utf-8")
    return root


class RootHygieneTests(unittest.TestCase):
    """A document at the root that answers "what is happening now" is a finding."""

    def test_the_repository_ships_none(self):
        # The reproduction: this is the case that failed on 2026-10-01, when
        # the root carried a 142-line `HANDOFF.md` and a 1384-line
        # `CHANGELOG.md`, and nothing read either.
        self.assertEqual(
            findings(REPO_ROOT), [],
            "the repository root carries agent-context sediment:\n  "
            + "\n  ".join(findings(REPO_ROOT)),
        )

    def test_a_session_document_at_the_root_is_found(self):
        for name in ("HANDOFF.md", "notes.md", "STATUS", "Progress.MD", "todo.md"):
            with tempfile.TemporaryDirectory() as root:
                path = make_repo(Path(root))
                (path / name).write_text("in flight\n", encoding="utf-8")
                self.assertTrue(
                    any(name in finding for finding in findings(path)),
                    f"{name} at the root is not a finding",
                )

    def test_a_session_document_below_the_root_is_not_a_finding(self):
        # The rule is about the root: a task's own notes under `research/` or a
        # skill's `SKILL.md` is a document with a pointer at it, which is the
        # shape the corpus is supposed to have.
        with tempfile.TemporaryDirectory() as root:
            path = make_repo(Path(root))
            (path / "research").mkdir()
            (path / "research" / "NOTES.md").write_text("in flight\n", encoding="utf-8")
            self.assertEqual(findings(path), [])

    def test_the_durable_documents_at_the_root_are_not_findings(self):
        # `AGENTS.md`, `README.md`, `RELEASING.md` and the generated
        # `arch-index.md` answer questions that do not go stale between
        # sessions. The list is a list, not a shape, precisely so this case is
        # a case.
        with tempfile.TemporaryDirectory() as root:
            path = make_repo(Path(root))
            for name in ("AGENTS.md", "README.md", "RELEASING.md", "arch-index.md"):
                (path / name).write_text("durable\n", encoding="utf-8")
            self.assertEqual(findings(path), [])


class ChangelogTests(unittest.TestCase):
    """One changelog, at the path the release checklist names."""

    def test_the_repository_ships_the_changelog_at_the_documented_path(self):
        self.assertTrue((REPO_ROOT / CHANGELOG_PATH).is_file(),
                        f"{CHANGELOG_PATH} is missing: the release artefact moved out of "
                        "the root and something has to read it there")

    def test_a_second_changelog_beside_the_documented_one_is_a_finding(self):
        # The duplicate the root relocation exists to prevent: a changelog
        # beside the one `RELEASING.md` names is a second answer to "what
        # shipped", and the release moves one of them.
        with tempfile.TemporaryDirectory() as root:
            path = make_repo(Path(root))
            (path / "CHANGELOG.md").write_text("# Changelog\n", encoding="utf-8")
            self.assertTrue(any("CHANGELOG.md" in finding for finding in findings(path)))

    def test_a_changelog_named_differently_is_not_read_as_one(self):
        # Matched on the exact name, like every other path in the repository:
        # a `CHANGELOG.draft.md` is somebody's scratch file and reads as one.
        with tempfile.TemporaryDirectory() as root:
            path = make_repo(Path(root))
            (path / "CHANGELOG.draft.md").write_text("# Changelog\n", encoding="utf-8")
            self.assertEqual(findings(path), [])

    def test_a_missing_changelog_is_a_finding(self):
        with tempfile.TemporaryDirectory() as root:
            path = make_repo(Path(root), changelog_at=None)
            self.assertTrue(any(CHANGELOG_PATH in finding for finding in findings(path)))


class VersionTests(unittest.TestCase):
    """`<Version>` and the changelog are one answer to "what is released"."""

    def test_a_release_above_the_product_version_is_a_finding(self):
        # The step `RELEASING.md` numbers 2 and 3 together: the heading is
        # written and the version was never bumped, so the tag says one thing
        # and the built assembly says another.
        with tempfile.TemporaryDirectory() as root:
            path = make_repo(Path(root), version="0.3.0",
                             changelog="# Changelog\n\n## [0.4.0] - 2026-10-01\n")
            self.assertTrue(any("0.4.0" in finding for finding in findings(path)))

    def test_the_current_version_and_unreleased_work_are_not_findings(self):
        # A released heading at the product version, and an `## [Unreleased]`
        # section above it that is not a release and so is not compared. There
        # is no such section in the file any more — `tools/changelog.py` is
        # the check on the hand-merge (ADR-0173) — but an `## [Unreleased]` is
        # this check's business only in that it must not be *compared*, and a
        # rule that compared it would fail a merge that had nothing to do with
        # it.
        with tempfile.TemporaryDirectory() as root:
            self.assertEqual(findings(make_repo(Path(root))), [])

    def test_a_version_is_compared_numerically(self):
        # `0.10.0` is above `0.9.0` and a string comparison says otherwise, so
        # the comparison is over numbers and not over the rendered heading.
        with tempfile.TemporaryDirectory() as root:
            path = make_repo(Path(root), version="0.9.0",
                             changelog="# Changelog\n\n## [0.10.0] - 2026-10-01\n")
            self.assertTrue(any("0.10.0" in finding for finding in findings(path)))
        with tempfile.TemporaryDirectory() as root:
            path = make_repo(Path(root), version="0.10.0",
                             changelog="# Changelog\n\n## [0.9.0] - 2026-09-01\n")
            self.assertEqual(findings(path), [])

    def test_an_unparseable_version_is_reported_rather_than_skipped(self):
        # A `<Version>` the read cannot parse is silence otherwise: every
        # comparison would pass and the check would be a no-op on the release
        # it exists to catch.
        with tempfile.TemporaryDirectory() as root:
            path = make_repo(Path(root), version="0.3.0-preview.1",
                             changelog="# Changelog\n\n## [0.4.0] - 2026-10-01\n")
            self.assertTrue(any("Directory.Build.props" in finding for finding in findings(path)))


class LaneWiringTests(unittest.TestCase):
    """The rule has to be read by the lanes that gate a merge.

    The shape ADR-0143 and ADR-0146 give the other two repository checks. A rule
    that only runs when `tools/**` changed is not a gate, and reintroducing a
    root session note is a docs or `src` change.
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
            self.assertIn("doc_surface_step", block,
                          f"the lane at {marker!r} does not run the repository-root check")

    def test_the_helper_runs_the_check(self):
        text = VERIFY_SH.read_text(encoding="utf-8")
        start = text.index("doc_surface_step() {")
        body = text[start:text.index("\n}", start)]
        self.assertIn("tools/doc_surface.py", body)

    def test_the_fast_lane_does_not_gate_it_on_tools_changing(self):
        text = VERIFY_SH.read_text(encoding="utf-8")
        default = self.lanes()["# --- the default lane"]
        conditional = default.index('if [[ "$RUN_TOOLING" == "1" ]]')
        self.assertIn("doc_surface_step", default[:conditional],
                      "the repository-root check has fallen inside the tools/**-only tooling gate")
        self.assertNotIn("doc_surface_step", default[conditional:])

    def test_ci_runs_the_check(self):
        # The CI `verify` job spells its steps out rather than calling the
        # script, so wiring the lanes alone would leave the detector that
        # follows every merge with the same hole.
        self.assertIn("tools/doc_surface.py", CI_YML.read_text(encoding="utf-8"))

    def test_the_repository_passes_the_check_as_a_command(self):
        # The detector as a command, which is how the lanes call it: a rule
        # only the test exercises is the same invisible hole one level up.
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--root", str(REPO_ROOT)],
            capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
