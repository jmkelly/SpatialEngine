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
import os
import subprocess
import sys
import tempfile
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from pathlib import Path

SCRIPT = Path(__file__).with_name("adr-next-number.py")
DECISIONS = "architecture/decisions"

# A branch reading the shared store while this tick reserves. --check asks
# holder() about every number in the tree, --list sweeps the whole store, and a
# concurrent --reserve walks the candidates, so a store under a six-branch
# coordinator tick is read constantly while it is written. The probe below is
# those reads with the git calls left out, in its own process, so the race it
# takes part in is between processes as it is in the swarm.
PROBE = """
import importlib.util, pathlib, sys, time

spec = importlib.util.spec_from_file_location("adr", sys.argv[1])
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
store, repo = pathlib.Path(sys.argv[2]), pathlib.Path(sys.argv[3])
first, count, seconds = int(sys.argv[4]), int(sys.argv[5]), float(sys.argv[6])
module.use_reservation_dir(store)
end = time.time() + seconds
while time.time() < end:
    for number in range(first, first + count):
        module.holder(repo, number, 14)
"""


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


def branch_repo(tmp) -> Path:
    """A repository with a `feature` branch, for the allocator's own calls."""
    repo = Path(tmp, "repo")
    repo.mkdir()
    git("init", "-q", "-b", "main", cwd=repo)
    (repo / "f.txt").write_text("x")
    commit(repo, "init")
    git("checkout", "-q", "-b", "feature", cwd=repo)
    return repo


def script_numbers(repo):
    """The numbers a record in the tree claims, for the tests to seed from."""
    return load_script().numbers_in(
        [f"{DECISIONS}/{path.name}"
         for path in Path(repo, DECISIONS).iterdir() if path.is_file()])


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
    is not the same as holding a number. The reservation is the hold: the name
    is taken exclusively, so exactly one branch can win it.
    """

    # A coordinator tick is not six branches reserving once: it is several
    # ticks racing, on a box already running the gates that read the store.
    # One round of six used to pass in isolation and fail inside eng/verify.sh,
    # which is how two branches were handed ADR-0010 by unrelated beads.
    WORKTREES = 8
    ROUNDS = 3
    # Numbers a probe asks about while the racers work: the one every racer is
    # about to try. A worker re-runs --check immediately before it writes its
    # record, and --check asks about the number it is about to take, so a
    # frontier number is the most-read path in the store — which is exactly
    # why it is also the most dangerous one to publish half-written.
    PROBES = 3

    def reserve(self, repo, *extra):
        return subprocess.run(
            ["python3", str(SCRIPT), "--reserve", *extra], cwd=repo,
            capture_output=True, text=True)

    def worktrees(self, seeded, count):
        links = []
        for index in range(count):
            path = Path(seeded.repo.parent, f"wt{index}")
            git("worktree", "add", "-q", "-b", f"b{index}", str(path),
                cwd=seeded.repo)
            links.append(path)
        return links

    def store_for(self, seeded, name):
        store = Path(seeded.repo.parent, f"store-{name}")
        store.mkdir()
        return store

    def probe(self, store, repo, frontier, seconds=30):
        """Processes reading the frontier of the store, the way a gate does."""
        return [subprocess.Popen(
            [sys.executable, "-c", PROBE, str(SCRIPT), str(store), str(repo),
             str(frontier), "1", str(seconds)], cwd=repo,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            for _ in range(self.PROBES)]

    @staticmethod
    def stop(probes):
        for probe in probes:
            probe.terminate()
            probe.wait()

    def burn_cpu(self, count):
        """Background processes competing for the machine, the way a swarm does."""
        burners = []
        for _ in range(count):
            process = subprocess.Popen(
                [sys.executable, "-c",
                 "import sys, time\n"
                 "end = time.time() + 30\n"
                 "while time.time() < end:\n"
                 "    sum(i * i for i in range(2000))\n"],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            burners.append(process)
        return burners

    def tick(self, seeded, links, store, frontier):
        """One tick: every link reserves at once while the store is read."""
        probes = self.probe(store, seeded.repo, frontier)
        try:
            with ThreadPoolExecutor(max_workers=len(links)) as pool:
                results = list(pool.map(
                    lambda link: self.reserve(link, "--reservation-dir", str(store)),
                    links))
        finally:
            self.stop(probes)
        self.assertEqual([r.returncode for r in results], [0] * len(links),
                         [r.stderr for r in results])
        return [r.stdout.strip() for r in results]

    def ticks(self, seeded):
        """Rounds of reservations against one shared store, and every number.

        The store is shared on purpose: it is the swarm's store, so a number
        handed out in one round is still spoken for in the next, and the whole
        run can be held to one rule — no number, ever, twice.
        """
        links = self.worktrees(seeded, self.WORKTREES * self.ROUNDS)
        store = self.store_for(seeded, "swarm")
        # The first number the seeded repository does not already hold, which
        # is where the first racers collide and where every later tick starts.
        first = max(script_numbers(seeded.repo), default=0) + 1
        numbers = []
        for round_index in range(self.ROUNDS):
            group = links[round_index * self.WORKTREES:
                          (round_index + 1) * self.WORKTREES]
            numbers += self.tick(seeded, group, store, first + len(numbers))
        return numbers

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
            numbers = self.ticks(seeded)
            self.assertEqual(len(numbers), self.WORKTREES * self.ROUNDS)
            self.assertEqual(len(set(numbers)), len(numbers), numbers)

    def test_parallel_branches_get_distinct_numbers_under_load(self):
        # The reproduction is load-sensitive: the window it needs is between a
        # claim's create and its write, so it only opens on a machine that is
        # already busy. eng/verify.sh is that machine, which is why the failure
        # landed on unrelated beads instead of on the allocator. Saturating the
        # cores here is what makes the test able to see it at all.
        with SeededRepo() as seeded:
            # Enough burners to keep every core busy, not so many that the
            # store is never read: this variant is about what load does to the
            # window between a claim's create and its write, not about
            # replacing the readers.
            burners = self.burn_cpu(os.cpu_count() or 4)
            try:
                numbers = self.ticks(seeded)
            finally:
                for burner in burners:
                    burner.terminate()
                    burner.wait()
            self.assertEqual(len(set(numbers)), len(numbers), numbers)

    def test_reserve_names_the_holder_so_a_collision_is_traceable(self):
        with SeededRepo() as seeded:
            self.reserve(seeded.repo, "--bead", "SpatialEngine-u2x.29")
            listed = subprocess.run(
                ["python3", str(SCRIPT), "--list"], cwd=seeded.repo,
                capture_output=True, text=True).stdout
            self.assertIn("0010", listed)
            self.assertIn("SpatialEngine-u2x.29", listed)


class ReservationAtomicityTests(unittest.TestCase):
    """A claim has to appear whole, or a reader takes it away (SpatialEngine-u2x.34).

    The reservation is a file in a directory every worktree shares, so it is
    the one piece of the store that changes while something else is reading
    it. It went wrong the way a shared mutable file goes wrong: `claim()` took
    the number with an exclusive create and wrote the body afterwards, and
    `holder()` — which every --check, --list and --reserve calls — read the
    gap as an unreadable record, called it stale and unlinked it. The branch
    that had just won the number was left holding a file that no longer
    existed, and the next branch took the same number. Two branches were told
    ADR-0010, which is the collision the store exists to prevent (ADR-0090).

    Both halves are checked here: a claim is never observable half-written,
    and a record that cannot be read is treated as held by somebody unknown
    rather than as free.
    """

    def setUp(self):
        self.script = load_script()
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.repo = branch_repo(self._tmp.name)
        self.store = Path(self._tmp.name, "store")
        self.store.mkdir()
        self.script.use_reservation_dir(self.store)

    def path_for(self, number):
        return self.store / f"{number:04d}.json"

    def test_a_reservation_is_never_observable_half_written(self):
        """A reader sees a claim, or nothing — never an empty or partial file."""
        observations = []
        finished = threading.Event()

        def reader():
            while not finished.is_set():
                for path in self.store.glob("*.json"):
                    observations.append(path.read_text())

        reader_thread = threading.Thread(target=reader, daemon=True)
        reader_thread.start()
        try:
            for number in range(1, 400):
                self.script.claim(self.repo, number, "feature", "b", "main")
        finally:
            finished.set()
            reader_thread.join()

        self.assertGreater(len(observations), 50, observations)
        for body in observations:
            try:
                record = json.loads(body)
            except ValueError:
                self.fail(f"a reader saw a reservation that is not JSON yet: {body!r}")
            self.assertEqual(record.get("branch"), "feature", body)
            self.assertIn("reserved_at", record, body)

    def test_a_reader_does_not_unlink_a_claim_that_is_being_written(self):
        """The claim a branch just won survives every reader that walks past."""
        number = 10
        path = self.path_for(number)
        lost = []
        for _ in range(200):
            path.unlink(missing_ok=True)
            claimed = threading.Event()

            def claim():
                self.script.claim(self.repo, number, "feature", "b", "main")
                claimed.set()

            claimer = threading.Thread(target=claim)
            claimer.start()
            while not claimed.is_set():
                self.script.holder(self.repo, number, 14)
            claimer.join()
            if not path.is_file():
                lost.append(number)
            path.unlink(missing_ok=True)

        self.assertEqual(lost, [], "a reader unlinked a live claim")

    def test_an_unreadable_reservation_is_held_by_somebody(self):
        """A record this process cannot read is an unknown holder, not a free number."""
        for body in ("", "{", '{"number": 10, "branch"', "not json at all"):
            with self.subTest(body=body):
                path = self.path_for(10)
                path.write_text(body)
                held = self.script.holder(self.repo, 10, 14)
                self.assertIsNotNone(held,
                                     "an unreadable record is still a hold")
                self.assertTrue(path.is_file(),
                                f"holder() unlinked a record it could not "
                                f"read: {body!r}")
                path.unlink()

    def test_another_branch_does_not_take_a_reservation_that_cannot_be_read(self):
        """The end-to-end version: the number stays spoken for."""
        with SeededRepo() as seeded:
            store = Path(seeded.repo.parent, "store")
            store.mkdir()
            (store / "0010.json").write_text('{"number": 10, "bran')
            linked = Path(seeded.repo.parent, "linked")
            git("worktree", "add", "-q", "-b", "other", str(linked),
                cwd=seeded.repo)
            got = subprocess.run(
                ["python3", str(SCRIPT), "--reserve",
                 "--reservation-dir", str(store)],
                cwd=linked, capture_output=True, text=True)
            self.assertEqual(got.returncode, 0, got.stderr)
            self.assertEqual(got.stdout.strip(), "0011")
            # ...and the unreadable record is left where it was, because the
            # branch that wrote it may still be running.
            self.assertTrue((store / "0010.json").is_file())

    def test_a_stale_reservation_is_still_swept(self):
        """The fix is not a wedge: a record read as dead is still removed."""
        self.script.claim(self.repo, 10, "deleted-branch", None, "main")
        self.assertIsNone(self.script.holder(self.repo, 10, 14))
        self.assertFalse(self.path_for(10).exists())

    def test_a_readable_record_with_no_holder_is_still_swept(self):
        """A record that parses but names nobody has been read, so it can go."""
        self.path_for(10).write_text("{}")
        self.assertIsNone(self.script.holder(self.repo, 10, 14))
        self.assertFalse(self.path_for(10).exists())

    def test_list_reports_a_reservation_it_cannot_read(self):
        """--list has to survive the store it cannot read every file in."""
        self.path_for(10).write_text("{ truncated")
        listed = subprocess.run(
            ["python3", str(SCRIPT), "--list", "--reservation-dir",
             str(self.store)],
            cwd=self.repo, capture_output=True, text=True)
        self.assertEqual(listed.returncode, 0, listed.stderr)
        self.assertIn("0010", listed.stdout)


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
        """A reservation file written by hand; corrupt=True writes a bare `{}`."""
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

    def test_a_reservation_naming_no_holder_does_not_wedge_allocation(self):
        # Readable, and it names nobody: a record whose branch is gone and
        # whose timestamp is missing has been read, so it can be judged dead
        # and swept. A record that cannot be *read* is a different case, and
        # the only one that must never be swept (SpatialEngine-u2x.34,
        # ReservationAtomicityTests).
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


class ReservationStoreLocationTests(unittest.TestCase):
    """A test cannot read the swarm's store and stay deterministic.

    The store is shared by every worktree on purpose, so it changes while a
    test runs: a live reservation for the number the numbering gate probes
    makes --check correctly refuse it, and the gate reports a violation for a
    record that does not exist (SpatialEngine-u2x.33). The store therefore has
    to be pointable somewhere private, without changing the default.
    """

    def test_the_default_is_still_the_shared_git_dir(self):
        script = load_script()
        with SeededRepo() as seeded:
            self.assertEqual(script.reservation_dir(seeded.repo).name,
                             "adr-reservations")
            self.assertEqual(script.reservation_dir(seeded.repo).parent.resolve(),
                             (seeded.repo / ".git").resolve())

    def test_a_named_store_is_used_instead_of_the_shared_one(self):
        with SeededRepo() as seeded:
            elsewhere = Path(tempfile.mkdtemp()) / "store"
            subprocess.run(
                ["python3", str(SCRIPT), "--reserve", "--reservation-dir", str(elsewhere)],
                cwd=seeded.repo, capture_output=True, text=True, check=True)
            self.assertTrue((elsewhere / "0010.json").is_file())
            # The shared store is untouched, so no worker can see the hold.
            self.assertEqual(subprocess.run(
                ["python3", str(SCRIPT), "--list"], cwd=seeded.repo,
                capture_output=True, text=True).stdout, "")

    def test_check_ignores_a_reservation_in_another_store(self):
        with SeededRepo() as seeded:
            shared = Path(tempfile.mkdtemp()) / "swarm"
            shared.mkdir()
            (shared / "0011.json").write_text(json.dumps({
                "number": 11, "branch": "other", "bead": "x", "base": "main",
                "reserved_at": datetime.now(timezone.utc).isoformat()}))
            mine = Path(tempfile.mkdtemp()) / "mine"
            mine.mkdir()
            got = subprocess.run(
                ["python3", str(SCRIPT), "--reservation-dir", str(mine),
                 "--check", "0011"],
                cwd=seeded.repo, capture_output=True, text=True)
            self.assertEqual(got.returncode, 0, got.stdout)
            self.assertEqual(got.stdout.strip(), "0011")

    def test_a_reservation_in_the_named_store_is_still_honoured(self):
        # Pointing the allocator elsewhere must not turn the store off, or the
        # override would be a way to skip the hold the swarm depends on.
        with SeededRepo() as seeded:
            store = Path(tempfile.mkdtemp()) / "store"
            subprocess.run(
                ["python3", str(SCRIPT), "--reserve", "--reservation-dir", str(store)],
                cwd=seeded.repo, capture_output=True, text=True, check=True)
            git("worktree", "add", "-q", "-b", "other",
                str(Path(seeded.repo.parent, "linked")), cwd=seeded.repo)
            got = subprocess.run(
                ["python3", str(SCRIPT), "--reservation-dir", str(store),
                 "--check", "0010"],
                cwd=Path(seeded.repo.parent, "linked"),
                capture_output=True, text=True)
            self.assertEqual(got.returncode, 1, got.stdout)
            self.assertIn("reserved", got.stdout)


if __name__ == "__main__":
    unittest.main()
