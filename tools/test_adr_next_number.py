#!/usr/bin/env python3
"""Tests for tools/adr-next-number.py.

Run: python3 -m unittest tools/test_adr_next_number.py

A number is only useful if it identifies exactly one record, so the numbering
suite (tests/architecture AdrNumberingTests) can only report a collision after
the branch merges. Allocation has to happen before the write, against a base
that already carries the numbers main took (SpatialEngine-u2x.29).
"""
import importlib.util
import json
import subprocess
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
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

    def test_check_accepts_several_numbers_at_once(self):
        with SeededRepo() as seeded:
            # The numbering suite asks about every record in the tree in one
            # run, so a per-number process would be a minute of gate time.
            got = self.run_tool(seeded.repo, "--check", "0007", "0011", "0009")
            self.assertEqual(got.returncode, 1, "0007 and 0009 have records")
            self.assertEqual(
                [line for line in got.stdout.splitlines()
                 if not line.startswith("ADR-")], ["0011"])
            self.assertIn("already taken", got.stdout)

    def test_check_is_all_or_nothing(self):
        with SeededRepo() as seeded:
            self.assertEqual(
                self.run_tool(seeded.repo, "--check", "0011", "0012").returncode, 0)

    def test_check_rejects_a_malformed_number(self):
        with SeededRepo() as seeded:
            got = self.run_tool(seeded.repo, "--check", "83")
            self.assertEqual(got.returncode, 2)
            self.assertIn("83", got.stderr)

    def test_an_unknown_base_is_an_error_not_a_guess(self):
        with SeededRepo() as seeded:
            got = self.run_tool(seeded.repo, "--base", "origin/nope")
            self.assertNotEqual(got.returncode, 0)
            self.assertIn("origin/nope", got.stderr)


class ReservationStoreTests(unittest.TestCase):
    """Reservations live in the shared git dir, not in a checkout.

    The swarm runs one linked worktree per branch over a single repository, so
    a store inside a worktree's own .git file would be private to that
    worktree and reserve nothing.
    """

    def test_store_is_in_the_git_common_dir(self):
        script = load_script()
        with SeededRepo() as seeded:
            store = script.reservation_dir(seeded.repo)
            self.assertEqual(store.parent.resolve(),
                             (seeded.repo / ".git").resolve())
            self.assertEqual(store.name, "adr-reservations")

    def test_a_linked_worktree_shares_the_store_with_the_main_checkout(self):
        script = load_script()
        with SeededRepo() as seeded:
            linked = Path(seeded.repo.parent, "linked")
            git("worktree", "add", "-q", "-b", "other", str(linked),
                cwd=seeded.repo)
            self.assertEqual(script.reservation_dir(linked).resolve(),
                             script.reservation_dir(seeded.repo).resolve())


class ReservationTests(unittest.TestCase):
    """A reservation is the step the numbering rule was missing.

    Four branches read one base and all took ADR-0075, because reading a base
    is not the same as holding a number. The reservation is the hold: the file
    is created with O_EXCL, so exactly one branch can win it.
    """

    def reserve(self, repo, *extra):
        return subprocess.run(
            ["python3", str(SCRIPT), "--reserve", *extra], cwd=repo,
            capture_output=True, text=True)

    def test_reserve_hands_out_the_next_free_number(self):
        with SeededRepo() as seeded:
            got = self.reserve(seeded.repo)
            self.assertEqual(got.returncode, 0)
            self.assertEqual(got.stdout.strip(), "0010")

    def test_reserve_skips_a_number_the_base_took_after_the_branch_started(self):
        with SeededRepo() as seeded:
            # main took ADR-0010 while this branch was working; the branch's
            # own tree has no record claiming it, so only the base knows.
            git("checkout", "-q", "main", cwd=seeded.repo)
            write_adr(seeded.repo, 10, "merged-elsewhere")
            commit(seeded.repo, "adr 10")
            git("checkout", "-q", "feature", cwd=seeded.repo)
            self.assertEqual(self.reserve(seeded.repo).stdout.strip(), "0011")

    def test_reserve_skips_a_number_another_branch_reserved(self):
        with SeededRepo() as seeded:
            self.assertEqual(self.reserve(seeded.repo).stdout.strip(), "0010")
            linked = Path(seeded.repo.parent, "linked")
            git("worktree", "add", "-q", "-b", "other", str(linked),
                cwd=seeded.repo)
            got = self.reserve(linked)
            self.assertEqual(got.returncode, 0)
            self.assertEqual(got.stdout.strip(), "0011")

    def test_reserving_the_same_number_twice_on_one_branch_is_idempotent(self):
        # The documented flow is: reserve at the start of the bead, re-run
        # --check immediately before writing, and re-reserve after a rebase.
        # Those runs must not consume a second number or fail.
        with SeededRepo() as seeded:
            first = self.reserve(seeded.repo).stdout.strip()
            second = self.reserve(seeded.repo).stdout.strip()
            self.assertEqual(first, second)
            self.assertEqual(
                self.reserve(seeded.repo).stdout.strip(), "0010")

    def test_parallel_branches_get_distinct_numbers(self):
        with SeededRepo() as seeded:
            links = []
            for index in range(6):
                path = Path(seeded.repo.parent, f"wt{index}")
                git("worktree", "add", "-q", "-b", f"b{index}", str(path),
                    cwd=seeded.repo)
                links.append(path)
            # All six race for the same number, as a swarm tick does.
            with ThreadPoolExecutor(max_workers=len(links)) as pool:
                results = list(pool.map(self.reserve, links))
            numbers = [r.stdout.strip() for r in results if r.returncode == 0]
            self.assertEqual(len(numbers), len(links),
                             [r.stderr for r in results if r.returncode])
            self.assertEqual(len(set(numbers)), len(numbers), numbers)

    def test_reserve_names_the_holder_so_a_collision_is_traceable(self):
        with SeededRepo() as seeded:
            self.reserve(seeded.repo, "--bead", "SpatialEngine-u2x.29")
            listed = subprocess.run(
                ["python3", str(SCRIPT), "--list"], cwd=seeded.repo,
                capture_output=True, text=True).stdout
            self.assertIn("0010", listed)
            self.assertIn("SpatialEngine-u2x.29", listed)


class CheckAgainstReservationsTests(unittest.TestCase):
    """--check is the gate a worker runs just before writing the record."""

    def run_tool(self, repo, *args):
        return subprocess.run(
            ["python3", str(SCRIPT), *args], cwd=repo,
            capture_output=True, text=True)

    def test_check_fails_for_a_number_another_branch_reserved(self):
        with SeededRepo() as seeded:
            self.run_tool(seeded.repo, "--reserve")
            linked = Path(seeded.repo.parent, "linked")
            git("worktree", "add", "-q", "-b", "other", str(linked),
                cwd=seeded.repo)
            got = self.run_tool(linked, "--check", "0010")
            self.assertEqual(got.returncode, 1)
            self.assertIn("reserved", got.stdout)

    def test_check_passes_for_the_number_this_branch_reserved(self):
        with SeededRepo() as seeded:
            self.assertEqual(
                self.run_tool(seeded.repo, "--reserve").stdout.strip(), "0010")
            got = self.run_tool(seeded.repo, "--check", "0010")
            self.assertEqual(got.returncode, 0)

    def test_a_reserved_number_is_not_claimed_until_the_record_exists(self):
        with SeededRepo() as seeded:
            self.run_tool(seeded.repo, "--reserve")
            linked = Path(seeded.repo.parent, "linked")
            git("worktree", "add", "-q", "-b", "other", str(linked),
                cwd=seeded.repo)
            # Reserving does not write a record: the tool must not start
            # counting a number as claimed, or the next allocation stalls.
            self.assertEqual(
                self.run_tool(linked, "--base", "main").stdout.strip(), "0010")


class ReleaseTests(unittest.TestCase):
    """A reservation has to end, or a renumbered branch holds a dead number."""

    def run_tool(self, repo, *args):
        return subprocess.run(
            ["python3", str(SCRIPT), *args], cwd=repo,
            capture_output=True, text=True)

    def test_release_frees_the_number_for_the_next_branch(self):
        with SeededRepo() as seeded:
            self.assertEqual(
                self.run_tool(seeded.repo, "--reserve").stdout.strip(), "0010")
            self.assertEqual(self.run_tool(seeded.repo, "--release").returncode, 0)
            linked = Path(seeded.repo.parent, "linked")
            git("worktree", "add", "-q", "-b", "other", str(linked),
                cwd=seeded.repo)
            self.assertEqual(
                self.run_tool(linked, "--reserve").stdout.strip(), "0010")

    def test_release_of_a_number_another_branch_holds_is_refused(self):
        with SeededRepo() as seeded:
            self.run_tool(seeded.repo, "--reserve")
            linked = Path(seeded.repo.parent, "linked")
            git("worktree", "add", "-q", "-b", "other", str(linked),
                cwd=seeded.repo)
            got = self.run_tool(linked, "--release", "0010")
            self.assertEqual(got.returncode, 1)
            self.assertIn("other", got.stdout + got.stderr)

    def test_releasing_a_number_this_branch_never_reserved_is_an_error(self):
        with SeededRepo() as seeded:
            self.assertNotEqual(
                self.run_tool(seeded.repo, "--release", "0042").returncode, 0)


class StaleReservationTests(unittest.TestCase):
    """A crashed worker must not hold a number for ever."""

    def run_tool(self, repo, *args):
        return subprocess.run(
            ["python3", str(SCRIPT), *args], cwd=repo,
            capture_output=True, text=True)

    def write_reservation(self, repo, number, branch, age_days=0,
                          corrupt=False):
        script = load_script()
        store = script.reservation_dir(repo)
        store.mkdir(parents=True, exist_ok=True)
        path = store / f"{number:04d}.json"
        body = "{}" if corrupt else json.dumps({
            "number": number, "branch": branch, "bead": None,
            "base": "main",
            "reserved_at": (datetime.now(timezone.utc)
                            - timedelta(days=age_days)).isoformat(),
        })
        path.write_text(body)
        return path

    def test_a_reservation_from_a_branch_that_no_longer_exists_is_stale(self):
        with SeededRepo() as seeded:
            self.write_reservation(seeded.repo, 10, "deleted-branch")
            # Nothing holds ADR-0010 any more, so it is handed out again
            # rather than left as a hole every later branch has to step over.
            self.assertEqual(
                self.run_tool(seeded.repo, "--reserve").stdout.strip(), "0010")

    def test_an_expired_reservation_is_stale(self):
        with SeededRepo() as seeded:
            self.write_reservation(seeded.repo, 10, "feature", age_days=30)
            got = self.run_tool(seeded.repo, "--reserve", "--max-age-days", "14")
            self.assertEqual(got.stdout.strip(), "0010")

    def test_a_corrupt_reservation_file_does_not_wedge_allocation(self):
        with SeededRepo() as seeded:
            self.write_reservation(seeded.repo, 10, "feature", corrupt=True)
            got = self.run_tool(seeded.repo, "--reserve")
            self.assertEqual(got.returncode, 0)
            self.assertEqual(got.stdout.strip(), "0010")

    def test_a_live_reservation_is_never_treated_as_stale(self):
        with SeededRepo() as seeded:
            # A branch that still exists, holding ADR-0010 three days ago.
            git("branch", "other", cwd=seeded.repo)
            self.write_reservation(seeded.repo, 10, "other", age_days=3)
            got = self.run_tool(seeded.repo, "--reserve", "--max-age-days", "14")
            self.assertEqual(got.stdout.strip(), "0011")

    def test_list_reports_the_holder_of_each_reservation(self):
        with SeededRepo() as seeded:
            self.write_reservation(seeded.repo, 10, "feature", age_days=3)
            listed = self.run_tool(seeded.repo, "--list").stdout
            self.assertIn("0010", listed)
            self.assertIn("feature", listed)


if __name__ == "__main__":
    unittest.main()
