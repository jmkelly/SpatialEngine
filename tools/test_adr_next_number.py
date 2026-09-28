#!/usr/bin/env python3
"""Tests for tools/adr-next-number.py.

Run: python3 -m unittest tools/test_adr_next_number.py

A number is only useful if it identifies exactly one record, so the numbering
suite (tests/architecture AdrNumberingTests) can only report a collision after
the branch merges. Allocation has to happen before the write, against a base
that already carries the numbers main took (SpatialEngine-u2x.29).
"""
import importlib.util
import subprocess
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("adr-next-number.py")
DECISIONS = "architecture/decisions"


def load_script():
    """Import the dashed script filename as a module."""
    spec = importlib.util.spec_from_file_location("adr_next_number", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def git(*args, cwd):
    return subprocess.run(
        ["git", *args], cwd=cwd, check=True, capture_output=True, text=True)


def write_adr(repo, number, slug="thing"):
    path = repo / DECISIONS / f"ADR-{number:04d}-{slug}.md"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(f"# ADR-{number:04d}: {slug}\n\n## Status\n\nAccepted\n")
    return path


def commit(repo, message="adr"):
    git("add", "-A", cwd=repo)
    git("-c", "user.email=t@e", "-c", "user.name=t",
        "commit", "-qm", message, cwd=repo)


class SeededRepo:
    """A repo on `main` holding decision records, plus a branch off it."""

    def __enter__(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.repo = Path(self._tmp.name, "repo")
        self.repo.mkdir()
        git("init", "-q", "-b", "main", cwd=self.repo)
        for number in (7, 9):
            write_adr(self.repo, number)
        commit(self.repo, "seed adrs")
        git("checkout", "-q", "-b", "feature", cwd=self.repo)
        return self

    def __exit__(self, *exc):
        self._tmp.cleanup()
        return False


class NumberParsingTests(unittest.TestCase):
    """File names are the record of a claim; only they count as one."""

    def test_reads_the_leading_number_of_a_decision_record(self):
        script = load_script()
        self.assertEqual(
            script.numbers_in([
                "architecture/decisions/ADR-0082-ingest-honours-the-source-crs.md",
                "architecture/decisions/ADR-0007-identity.md",
            ]),
            {7, 82})

    def test_ignores_files_that_are_not_decision_records(self):
        script = load_script()
        self.assertEqual(
            script.numbers_in([
                "architecture/decisions/README.md",
                # Two digits, a draft below the directory, a source file: none
                # of them is a record claiming a number. The numbers are live
                # ones so this file cites records that exist.
                "architecture/decisions/ADR-82-too-short.md",
                "architecture/decisions/drafts/ADR-0009-not-at-top-level.md",
                "src/ADR-0007-not-a-record.md",
            ]),
            set())


class NextFreeNumberTests(unittest.TestCase):
    """Allocation is monotonic, so a gap left by a renumber is not refilled."""

    def test_one_past_the_highest_claim(self):
        self.assertEqual(load_script().next_free_number({1, 2, 3}), 4)

    def test_gaps_are_not_refilled(self):
        # Filling a hole hands the same number to every branch that sees it,
        # which is the collision this tool exists to prevent.
        self.assertEqual(load_script().next_free_number({1, 2, 9}), 10)

    def test_an_empty_repository_starts_at_one(self):
        self.assertEqual(load_script().next_free_number(set()), 1)

    def test_a_number_written_on_this_branch_counts_as_claimed(self):
        self.assertEqual(load_script().next_free_number({7, 9, 10}), 11)


class BaseRefTests(unittest.TestCase):
    """The base is main, resolved the way a worktree sees it."""

    def test_prefers_origin_main_then_main(self):
        script = load_script()
        with SeededRepo() as seeded:
            self.assertEqual(script.resolve_base_ref(seeded.repo), "main")
            git("update-ref", "refs/remotes/origin/main", "main",
                cwd=seeded.repo)
            self.assertEqual(script.resolve_base_ref(seeded.repo), "origin/main")

    def test_falls_back_to_head_when_there_is_no_main(self):
        script = load_script()
        with tempfile.TemporaryDirectory() as tmp:
            repo = Path(tmp, "repo")
            repo.mkdir()
            git("init", "-q", "-b", "trunk", cwd=repo)
            (repo / "f.txt").write_text("x")
            commit(repo, "init")
            self.assertEqual(script.resolve_base_ref(repo), "HEAD")


class ClaimedNumberTests(unittest.TestCase):
    """A claim is a number on the base *or* an unmerged file in the tree."""

    def test_reads_both_the_base_and_the_working_tree(self):
        script = load_script()
        with SeededRepo() as seeded:
            # Committed on the branch: main does not have it.
            write_adr(seeded.repo, 10, "unmerged")
            claimed = script.claimed_numbers(seeded.repo, "main")
            self.assertEqual(claimed, {7, 9, 10})

    def test_an_uncommitted_record_counts(self):
        script = load_script()
        with SeededRepo() as seeded:
            write_adr(seeded.repo, 11, "still-being-written")
            self.assertIn(11, script.claimed_numbers(seeded.repo, "main"))


class CommandLineTests(unittest.TestCase):
    """The tool is run by an agent mid-task, so exit codes carry the verdict."""

    def run_tool(self, repo, *args):
        return subprocess.run(
            ["python3", str(SCRIPT), *args], cwd=repo,
            capture_output=True, text=True)

    def test_prints_the_next_free_number(self):
        with SeededRepo() as seeded:
            got = self.run_tool(seeded.repo, "--base", "main")
            self.assertEqual(got.returncode, 0)
            self.assertEqual(got.stdout.strip(), "0010")

    def test_defaults_to_the_resolved_base(self):
        with SeededRepo() as seeded:
            # main has moved on: ADR-0020 is on origin/main only, exactly the
            # case where reading the branch point hands out a taken number.
            git("checkout", "-q", "-b", "advanced", cwd=seeded.repo)
            write_adr(seeded.repo, 20, "already-merged-elsewhere")
            commit(seeded.repo, "adr 20")
            git("update-ref", "refs/remotes/origin/main", "advanced",
                cwd=seeded.repo)
            git("checkout", "-q", "feature", cwd=seeded.repo)
            got = self.run_tool(seeded.repo)
            self.assertEqual(got.returncode, 0)
            self.assertEqual(got.stdout.strip(), "0021")

    def test_check_fails_for_a_number_the_base_already_took(self):
        with SeededRepo() as seeded:
            got = self.run_tool(seeded.repo, "--check", "0007")
            self.assertEqual(got.returncode, 1)
            self.assertIn("0007", got.stdout)

    def test_check_fails_for_a_number_this_branch_already_wrote(self):
        with SeededRepo() as seeded:
            write_adr(seeded.repo, 10, "unmerged")
            self.assertEqual(
                self.run_tool(seeded.repo, "--check", "0010").returncode, 1)

    def test_check_passes_for_a_free_number(self):
        with SeededRepo() as seeded:
            got = self.run_tool(seeded.repo, "--check", "0011")
            self.assertEqual(got.returncode, 0)
            self.assertEqual(got.stdout.strip(), "0011")

    def test_an_unknown_base_is_an_error_not_a_guess(self):
        with SeededRepo() as seeded:
            got = self.run_tool(seeded.repo, "--base", "origin/nope")
            self.assertNotEqual(got.returncode, 0)
            self.assertIn("origin/nope", got.stderr)


if __name__ == "__main__":
    unittest.main()
