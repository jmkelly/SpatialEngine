#!/usr/bin/env python3
"""The release section is generated from git; nothing hand-merges it (ADR-0173).

Run: python3 -m unittest tools/test_changelog.py

`docs/CHANGELOG.md` grew a `## [Unreleased]` section of 1038 lines,
hand-merged from merge to merge by a rule no gate touched: 79 commits touched
it in the 17 days after `v0.3.0` and 107 merges landed in that span. ADR-0148
deferred generating it, with measurements, and left the question this file
answers — *what committed input, identical on a feature branch and on `main`,
can say what shipped since the last release?*

The answer is the one thing a merge writes down and nothing else does: the
commits. A work commit carries the bead id in a `Task:` trailer, the bead title
as its subject, the `ADR-NNNN` ids the change cites, and the narrative the
agent wrote for it; a merge commit carries `Merge <bead>: <title>`. That is a
git-only input, so it reads the same on a feature branch, on `main` and in a CI
clone — unlike the bead queue, which is gitignored and in the git common dir
and is therefore absent from every CI clone.

The section is generated **at release time and nowhere else**, which is what
makes it a gate that does not red on every merge. A merge-scoped generated
section would need a `--write` and a follow-up commit per merge, and the lane
between them would be red — the cost ADR-0134 took off the merge path on
purpose. So nothing is generated between releases: the release step renders the
section from `<previous tag>..<tag>` and writes it, and the check every lane
runs is the one that is true on every merge — no `## [Unreleased]` heading is
hand-maintained between releases. That is a check the lanes can call directly,
because a hand-merge is a `docs` change (ADR-0146) and because it cannot be
red on a merge that did not hand-merge anything.

The rules live in `tools/changelog.py`, which is what the lanes call
(LaneWiringTests below): one rule, one implementation, because a test carrying
a second copy of it can pass while the check every merge runs fails.
"""
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

# The path is set by the runner; the rules live in the tool the lanes call.
from tools.changelog import (  # noqa: E402
    CHANGELOG_PATH,
    findings,
    render_release_section,
    verify_release,
    write_release_section,
)

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPT = REPO_ROOT / "tools" / "changelog.py"
VERIFY_SH = REPO_ROOT / "eng" / "verify.sh"
CI_YML = REPO_ROOT / ".github" / "workflows" / "ci.yml"
RELEASING = REPO_ROOT / "RELEASING.md"

#: A changelog with the released history and nothing hand-merged above it.
RELEASED_CHANGELOG = (
    "# Changelog\n"
    "\n"
    "Prose above the sections.\n"
    "\n"
    "## [0.3.0] - 2026-09-15\n"
    "\n"
    "- **Older work** (SpatialEngine-aaa)\n"
    "\n"
    "## [0.2.0] - 2026-08-01\n"
    "\n"
    "- **Older still** (SpatialEngine-bbb)\n"
)

GIT_ENV = {
    "GIT_AUTHOR_NAME": "Test", "GIT_AUTHOR_EMAIL": "test@example.invalid",
    "GIT_COMMITTER_NAME": "Test", "GIT_COMMITTER_EMAIL": "test@example.invalid",
    "GIT_CONFIG_GLOBAL": "/dev/null", "GIT_CONFIG_SYSTEM": "/dev/null",
}


def git(repo: Path, *arguments: str) -> str:
    """Run git in a throwaway repository, with no user configuration."""
    return subprocess.run(
        ["git", "-C", str(repo), *arguments],
        check=True, capture_output=True, text=True, env={**os.environ, **GIT_ENV},
    ).stdout


def write_commit(repo: Path, subject: str, body: str) -> str:
    git(repo, "commit", "--quiet", "--allow-empty", "-m", subject, "-m", body)
    return git(repo, "rev-parse", "HEAD").strip()


def merge_bead(repo: Path, bead: str, title: str, body: str) -> None:
    """A bead the way `tools/bd-merge-bead.py` lands one: work on a branch, a
    merge commit on `main` naming the bead and the title, carrying the same
    `Task:` trailer as the work it brought in."""
    git(repo, "checkout", "--quiet", "-b", f"bead/{bead}")
    write_commit(repo, title, body)
    git(repo, "checkout", "--quiet", "main")
    write_commit(repo, "Advance main", "So the bead merge is a real merge.\n")
    git(repo, "merge", "--quiet", "--no-ff",
        "-m", f"Merge {bead}: {title}", "-m", f"Task: {bead}", f"bead/{bead}")


def make_repo(root: Path, changelog: str | None = None, version: str = "0.4.0") -> Path:
    """A throwaway tree shaped like this repository, with one release behind it."""
    (root / "docs").mkdir(parents=True, exist_ok=True)
    (root / "docs" / "CHANGELOG.md").write_text(
        RELEASED_CHANGELOG if changelog is None else changelog, encoding="utf-8")
    (root / "Directory.Build.props").write_text(
        "<Project>\n  <PropertyGroup>\n"
        f"    <Version>{version}</Version>\n"
        "  </PropertyGroup>\n</Project>\n", encoding="utf-8")
    git(root, "init", "--quiet", "--initial-branch", "main")
    write_commit(root, "First release", "Task: SpatialEngine-aaa")
    git(root, "tag", "v0.3.0")
    return root


class RenderTests(unittest.TestCase):
    """A range of git is the whole input a release section is rendered from."""

    def setUp(self):
        self._temporary = tempfile.TemporaryDirectory()
        self.repo = make_repo(Path(self._temporary.name))
        merge_bead(self.repo, "SpatialEngine-ufn",
                   "Krovak stays out on its axes (ADR-0170)",
                   "ADR-0170 records the decision; ADR-0086 amended in place.\n\n"
                   "The check is on the axes rather than the method.\n\n"
                   "Task: SpatialEngine-ufn")
        merge_bead(self.repo, "SpatialEngine-2ve", "Serve spatialRel in the protocol's frame",
                   "`spatialRel` names the relation of the feature to the input geometry, and a "
                   "live FeatureServer agrees.\n\n"
                   "Task: SpatialEngine-2ve\n")
        write_commit(self.repo, "A commit with no bead of its own", "No trailer here.\n")
        self.addCleanup(self._temporary.cleanup)

    def section(self, version: str = "0.4.0", date: str = "2026-10-01") -> str:
        return render_release_section(self.repo, "v0.3.0..HEAD", version, date)

    def test_the_heading_carries_the_version_and_the_date(self):
        self.assertTrue(self.section().startswith("## [0.4.0] - 2026-10-01\n"))

    def test_a_merged_bead_is_one_entry_with_the_title_of_its_merge(self):
        entries = [entry for entry in self.section().split("\n\n") if "Krovak" in entry]
        self.assertEqual(len(entries), 1, self.section())
        self.assertIn("Krovak stays out on its axes", entries[0])
        self.assertIn("SpatialEngine-ufn", entries[0])

    def test_the_narrative_the_commit_carries_is_the_entry_body(self):
        self.assertIn("The check is on the axes rather than the method.", self.section())

    def test_the_adr_ids_the_change_cited_are_on_the_entry(self):
        self.assertIn("ADR-0170", self.section())
        self.assertIn("ADR-0086", self.section())

    def test_the_task_trailer_is_not_the_entry_body(self):
        self.assertNotIn("Task: SpatialEngine-ufn", self.section())

    def test_the_merge_commit_does_not_duplicate_the_work_commit(self):
        # The merge carries the same `Task:` trailer as the work it brought in;
        # a bead is one entry however many commits landed it.
        self.assertEqual(self.section().count("Krovak stays out on its axes"), 1)

    def test_a_commit_with_no_bead_still_ships(self):
        self.assertIn("A commit with no bead of its own", self.section())

    def test_the_release_commit_is_not_a_change_that_shipped(self):
        # `RELEASING.md` step 5 tags the commit that carries the section, and
        # that commit is named by a `Release:` trailer rather than by a
        # convention about where the tag sits — so re-rendering the range after
        # the tag exists gives the same section it gave before it.
        write_commit(self.repo, "Release 0.4.0",
                     "The changelog and the version.\n\nRelease: 0.4.0\n")
        self.assertNotIn("Release 0.4.0", self.section())

    def test_the_newest_entry_comes_first(self):
        text = self.section()
        self.assertLess(text.index("A commit with no bead of its own"),
                        text.index("Krovak stays out on its axes"))

    def test_a_range_is_the_only_range_read(self):
        # v0.3.0..v0.3.0 is the release behind us and nothing else: the first
        # commit in the fixture's history is tagged v0.3.0.
        self.assertEqual(render_release_section(self.repo, "v0.3.0..v0.3.0", "0.4.0", "2026-10-01"), "")

    def test_a_range_with_nothing_in_it_renders_nothing(self):
        self.assertEqual(render_release_section(self.repo, "v0.3.0..v0.3.0", "0.4.0", "2026-10-01"), "")


class WriteTests(unittest.TestCase):
    """`RELEASING.md` step 3 writes the section; the writer is idempotent."""

    def setUp(self):
        self._temporary = tempfile.TemporaryDirectory()
        self.repo = make_repo(Path(self._temporary.name))
        write_commit(self.repo, "Serve spatialRel in the protocol's frame",
                     "The frame is the feature's.\n\nTask: SpatialEngine-2ve")
        self.addCleanup(self._temporary.cleanup)

    def test_the_section_lands_above_the_newest_release(self):
        write_release_section(self.repo, "v0.3.0..HEAD", "0.4.0", "2026-10-01")
        written = (self.repo / CHANGELOG_PATH).read_text(encoding="utf-8")
        self.assertLess(written.index("## [0.4.0]"), written.index("## [0.3.0]"))
        self.assertLess(written.index("## [0.4.0]"), written.index("## [0.2.0]"))
        self.assertIn("Serve spatialRel in the protocol's frame", written)

    def test_the_prose_above_the_sections_is_kept(self):
        write_release_section(self.repo, "v0.3.0..HEAD", "0.4.0", "2026-10-01")
        self.assertIn("Prose above the sections.",
                      (self.repo / CHANGELOG_PATH).read_text(encoding="utf-8"))

    def test_writing_the_same_release_twice_replaces_it_rather_than_duplicating(self):
        write_release_section(self.repo, "v0.3.0..HEAD", "0.4.0", "2026-10-01")
        write_release_section(self.repo, "v0.3.0..HEAD", "0.4.0", "2026-10-01")
        written = (self.repo / CHANGELOG_PATH).read_text(encoding="utf-8")
        self.assertEqual(written.count("## [0.4.0]"), 1, written)

    def test_a_hand_merged_unreleased_section_is_replaced_not_joined(self):
        # The transition this bead makes: a release step run on a tree that
        # still carries the hand-merged section must not leave it behind as a
        # second answer to "what shipped since the last release".
        path = self.repo / CHANGELOG_PATH
        path.write_text("# Changelog\n\n## [Unreleased]\n\n- **Hand-merged**\n\n"
                        + RELEASED_CHANGELOG.split("\n", 2)[2], encoding="utf-8")
        write_release_section(self.repo, "v0.3.0..HEAD", "0.4.0", "2026-10-01")
        written = path.read_text(encoding="utf-8")
        self.assertNotIn("## [Unreleased]", written)
        self.assertNotIn("Hand-merged", written)


class HandMergeTests(unittest.TestCase):
    """The check every lane runs: nothing hand-merges between releases."""

    def test_a_hand_merged_unreleased_section_is_a_finding(self):
        with tempfile.TemporaryDirectory() as root:
            path = make_repo(Path(root),
                             changelog="# Changelog\n\n## [Unreleased]\n\n- **Shipped**\n\n"
                                       "## [0.3.0] - 2026-09-15\n\n- **Older**\n")
            self.assertTrue(any("Unreleased" in finding for finding in findings(path)))

    def test_the_repository_ships_no_hand_merged_unreleased_section(self):
        # The reproduction: `## [Unreleased]` was 1038 lines of a
        # `docs/CHANGELOG.md`, hand-merged by a rule no gate touched, and
        # generated instead by `RELEASING.md` step 3.
        self.assertEqual(
            findings(REPO_ROOT), [],
            "docs/CHANGELOG.md carries a hand-merged section:\n  " + "\n  ".join(findings(REPO_ROOT)))

    def test_a_generated_release_section_is_not_a_finding(self):
        with tempfile.TemporaryDirectory() as root:
            path = make_repo(Path(root))
            write_commit(path, "Work", "Body.\n\nTask: SpatialEngine-2ve")
            write_release_section(path, "v0.3.0..HEAD", "0.4.0", "2026-10-01")
            self.assertEqual(findings(path), [])

    def test_an_empty_unreleased_section_is_still_a_heading(self):
        # `## [Unreleased]` with nothing under it is the shape somebody starts
        # a hand-merge with, so the heading is the finding, not the prose.
        with tempfile.TemporaryDirectory() as root:
            path = make_repo(Path(root),
                             changelog="# Changelog\n\n## [Unreleased]\n\n## [0.3.0] - 2026-09-15\n")
            self.assertTrue(findings(path))


class VerifyReleaseTests(unittest.TestCase):
    """Release-time verification: the written section is the generated one."""

    def setUp(self):
        self._temporary = tempfile.TemporaryDirectory()
        self.repo = make_repo(Path(self._temporary.name))
        write_commit(self.repo, "Serve spatialRel in the protocol's frame",
                     "The frame is the feature's.\n\nTask: SpatialEngine-2ve")
        write_release_section(self.repo, "v0.3.0..HEAD", "0.4.0", "2026-10-01")
        self.git_tag_the_release()
        self.addCleanup(self._temporary.cleanup)

    def git_tag_the_release(self):
        write_commit(self.repo, "Release 0.4.0", "The changelog and the version.\n\nRelease: 0.4.0\n")
        git(self.repo, "tag", "v0.4.0")

    def test_the_generated_section_verifies(self):
        self.assertEqual(verify_release(self.repo, "0.4.0"), [])

    def test_an_edited_release_section_is_reported(self):
        path = self.repo / CHANGELOG_PATH
        path.write_text(path.read_text(encoding="utf-8").replace(
            "The frame is the feature's.", "Something else entirely."), encoding="utf-8")
        self.assertTrue(any("0.4.0" in finding for finding in verify_release(self.repo, "0.4.0")))

    def test_a_release_with_no_tag_is_not_judged(self):
        # The tags live outside the shallow clone CI does by default; a check
        # that failed on a missing tag would be a false red on the job that
        # follows every release, so it says "not judged" instead.
        with tempfile.TemporaryDirectory() as root:
            path = make_repo(Path(root))
            write_commit(path, "Work", "Body.\n\nTask: SpatialEngine-2ve")
            write_release_section(path, "v0.3.0..HEAD", "0.4.0", "2026-10-01")
            self.assertEqual(verify_release(path, "0.4.0"), [], "a release with no tag is not judged")


class LaneWiringTests(unittest.TestCase):
    """The check has to be read by the lanes that gate a merge.

    The shape ADR-0143 and ADR-0146 give the other two repository checks, and
    ADR-0148 gives `tools/doc_surface.py`: a rule that only runs when `tools/**`
    changed is not a gate, and a hand-merge is a `docs` change.
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
            end = markers[index + 1] if index + 1 < len(markers) else None
            blocks[marker] = text[text.index(marker):text.index(end)] if end else text[text.index(marker):]
        return blocks

    def test_every_verify_lane_runs_the_check(self):
        for marker, block in self.lanes().items():
            self.assertIn("changelog_step", block,
                          f"the lane at {marker!r} does not run the changelog check")

    def test_the_helper_runs_the_check(self):
        text = VERIFY_SH.read_text(encoding="utf-8")
        start = text.index("changelog_step() {")
        body = text[start:text.index("\n}", start)]
        self.assertIn("tools/changelog.py --check", body)

    def test_the_fast_lane_does_not_gate_it_on_tools_changing(self):
        text = VERIFY_SH.read_text(encoding="utf-8")
        default = self.lanes()["# --- the default lane"]
        conditional = default.index('if [[ "$RUN_TOOLING" == "1" ]]')
        self.assertIn("changelog_step", default[:conditional],
                      "the changelog check has fallen inside the tools/**-only tooling gate")
        self.assertNotIn("changelog_step", default[conditional:])

    def test_ci_runs_the_check(self):
        self.assertIn("tools/changelog.py --check", CI_YML.read_text(encoding="utf-8"))

    def test_the_release_checklist_generates_the_section(self):
        self.assertIn("tools/changelog.py", RELEASING.read_text(encoding="utf-8"))

    def test_the_repository_passes_the_check_as_a_command(self):
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--root", str(REPO_ROOT), "--check"],
            capture_output=True, text=True,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
