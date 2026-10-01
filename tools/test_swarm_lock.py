#!/usr/bin/env python3
"""The swarm's file-overlap lock is derived, and the read list is resolved once.

Run: python3 -m unittest tools/test_swarm_lock.py

`SpatialEngine-imz.6`. `eng/swarm-runbook.md` used to hand the coordinator its
ordering rule — "`.2` before `.16`, `.4` before `.21`" — for a rule the repo
already knew was incomplete: new overlapping beads appear every run, which is
why recovery keeps being needed. A hand-listed rule is stale the moment the
next bead lands, and it fails silently: the overlap is simply not scheduled
against anything.

So the lock is computed from what the beads say, and the worker prompt carries
the routing *resolved* rather than the index to resolve it from:

  * `mentioned_paths` reads the files out of a bead's own text, and
    `paths_overlap` decides whether two beads are in the same place — the
    properties that make the answer worth having;
  * `plan` answers "may this bead start", naming the in-flight bead it
    collides with and the files they share, so the coordinator skips one
    instead of merging it later;
  * `read_list` resolves a bead's ADR list once, from the citations the bead
    itself carries and the routing rows in `architecture/distilled/README.md`,
    and prints the row it matched so a reader can see why;
  * `metrics` counts the four incidents the bead says to count per drain
    cycle, because a lock that cannot be measured is a lock nobody can tell
    is working.

The last group is the runbook's own text. Prose that has already failed is
advice, so the hand-listed ordering is asserted *absent* and the derived
command asserted present: a runbook that quietly regains a hand-listed pair
order has undone the mechanism this tool exists for.
"""
import io
import json
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO_ROOT))

from tools.swarm_lock import (  # noqa: E402
    bead_files,
    is_in_flight,
    main,
    mentioned_paths,
    metrics,
    parse_routing,
    paths_overlap,
    plan,
    read_list,
)

RUNBOOK = REPO_ROOT / "eng" / "swarm-runbook.md"
DISTILLED_README = REPO_ROOT / "architecture" / "distilled" / "README.md"

SOURCE = """\
some prose naming and/or a ratio, ADR-0134 and SpatialEngine-u2x.2 are not \
paths, and neither is https://example.com/a/b.html or the word src alone.

  * `architecture/decisions/` and src/Spatial.Core/Geometry/Point.cs.
  * tools/adr-next-number.py --check NNNN (see tools/*.py)
  * r"src\\Spatial.Core\\Geometry\\Point.cs" for the Windows spelling
  * eng/verify.sh:412 for a line
"""


def git(root: Path, *args: str) -> str:
    done = subprocess.run(["git", *args], cwd=root, capture_output=True,
                          text=True, check=True)
    return done.stdout.strip()


def commit(root: Path, message: str, body: str = "") -> str:
    message_file = root / "COMMIT_EDITMSG"
    message_file.write_text(f"{message}\n\n{body}\n" if body else f"{message}\n",
                            encoding="utf-8")
    git(root, "add", "-A")
    git(root, "commit", "-q", "-F", str(message_file))
    return git(root, "rev-parse", "HEAD")


def make_repo(root: Path) -> Path:
    git(root, "init", "-q", "-b", "main")
    git(root, "config", "user.email", "lock@example.com")
    git(root, "config", "user.name", "Lock")
    (root / "README.md").write_text("# fixture\n", encoding="utf-8")
    commit(root, "initial commit")
    return root


def bead(bead_id, title="", description="", status="open", labels=()):
    return {
        "id": bead_id,
        "title": title,
        "description": description,
        "status": status,
        "labels": list(labels),
    }


def routing_markdown() -> str:
    """A two-row route table in the shape `architecture/distilled/README.md` uses."""
    return textwrap.dedent("""\
        # Distilled Architecture

        ## Route by task

        | Task | Read | Related ADRs |
        | --- | --- | --- |
        | Esri GeoServices REST (serve/consume) | `host-and-clients.md`, `../references/x.md` | 0035, 0037, 0048, 0112 |
        | Data stores (PostGIS, SQL Server) | `plugins.md` | 0010, 0072, 0074 |
        | HTTP API, config, SDKs, frontend | `host-and-clients.md` | 0014–0019, 0033 |
        | Any architectural change | this file | — |
        """)


class MentionedPathsTests(unittest.TestCase):
    """The files a bead's own text names — the whole input to the lock."""

    def test_the_repository_paths_are_read_out_of_prose(self):
        self.assertEqual(
            mentioned_paths(SOURCE),
            ["architecture/decisions/", "src/Spatial.Core/Geometry/Point.cs",
             "tools/adr-next-number.py", "tools/*.py",
             "src/Spatial.Core/Geometry/Point.cs", "eng/verify.sh"])

    def test_a_ratio_and_a_bead_id_are_not_paths(self):
        # "and/or" and "here/or/there" are the shape a naive slash-regex takes
        # for a file, and a lock built on those would block half the queue.
        found = mentioned_paths("and/or, here/or/there, in/flight, and/or/none")
        self.assertEqual(found, [])

    def test_a_url_is_not_a_repository_path(self):
        self.assertEqual(
            mentioned_paths("see https://example.com/a/b.html for the spec"),
            [])

    def test_an_adr_citation_is_not_a_path(self):
        self.assertEqual(mentioned_paths("ADR-0134 and ADR-0174"), [])

    def test_the_windows_spelling_is_the_same_path(self):
        self.assertEqual(
            mentioned_paths("src\\Spatial.Core\\Geometry\\Point.cs"),
            ["src/Spatial.Core/Geometry/Point.cs"])

    def test_a_branch_name_is_not_a_file(self):
        # Measured on the real queue: every worker's notes carry its own
        # `bd/<id>` branch, so reading a branch as a file gave two beads a
        # phantom overlap on `bd/SpatialEngine-u2x.11` and would have blocked
        # work that touched nothing in common.
        self.assertEqual(
            mentioned_paths("branch bd/SpatialEngine-u2x.11 off origin/main"),
            [])

    def test_a_git_range_is_not_a_file(self):
        self.assertEqual(mentioned_paths("origin/main..main, bd/x...HEAD"), [])

    def test_a_prose_list_of_ids_is_not_a_file(self):
        self.assertEqual(mentioned_paths("behind imz.4/imz.9/u2x.59"), [])


class OverlapTests(unittest.TestCase):
    """Whether two named paths are in the same place."""

    def test_the_same_file_overlaps(self):
        self.assertTrue(paths_overlap("tools/verify_scope.py",
                                      "tools/verify_scope.py"))

    def test_a_directory_overlaps_what_is_under_it(self):
        self.assertTrue(paths_overlap(
            "architecture/decisions/", "architecture/decisions/0174-lock.md"))

    def test_a_prefix_that_is_not_a_segment_boundary_does_not_overlap(self):
        self.assertFalse(paths_overlap("src/Spatial.Core/",
                                       "src/Spatial.CoreEx/Point.cs"))

    def test_a_glob_matches_under_it(self):
        self.assertTrue(paths_overlap("src/**", "src/Spatial.Core/Point.cs"))
        self.assertTrue(paths_overlap("tools/*.py", "tools/adr-next-number.py"))

    def test_two_unrelated_files_do_not_overlap(self):
        self.assertFalse(paths_overlap("tools/verify_scope.py",
                                       "src/Spatial.Core/Point.cs"))


class PlanTests(unittest.TestCase):
    """The lock itself: which ready bead may start, and why not."""

    def test_a_ready_bead_sharing_a_file_with_an_in_flight_one_is_blocked(self):
        ready = [bead("SpatialEngine-x.2", "reads a filter",
                      "amend src/Spatial.Stores.PostGIS/QueryPushdown.cs")]
        flying = [bead("SpatialEngine-x.1", status="in_progress",
                       description="fix src/Spatial.Stores.PostGIS/QueryPushdown.cs")]
        verdicts = plan(ready, flying)
        self.assertEqual(len(verdicts), 1)
        self.assertTrue(verdicts[0].blocked)
        self.assertEqual(verdicts[0].blocked_by, ["SpatialEngine-x.1"])
        self.assertEqual(
            verdicts[0].shared,
            ["src/Spatial.Stores.PostGIS/QueryPushdown.cs"])

    def test_a_ready_bead_touching_different_files_may_start(self):
        ready = [bead("SpatialEngine-y.1", description="tools/verify_scope.py")]
        flying = [bead("SpatialEngine-x.1", status="in_progress",
                       description="src/Spatial.Core/Point.cs")]
        verdict = plan(ready, flying)[0]
        self.assertFalse(verdict.blocked)
        self.assertEqual(verdict.blocked_by, [])

    def test_a_bead_naming_no_file_is_reported_as_ordered_by_hand(self):
        # The lock cannot invent a file set; saying so is the honest answer,
        # and the coordinator decides rather than the rule guessing.
        verdict = plan([bead("SpatialEngine-z.1", "tidy the prose")], [])[0]
        self.assertEqual(verdict.ordered_by_hand, True)
        self.assertFalse(verdict.blocked)

    def test_a_closed_bead_holds_nothing_whatever_its_labels(self):
        # `bd list --all` carries every bead the swarm ever ran, and a label
        # from its time is never taken off: on the real queue 122 beads read
        # as in flight until closed ones were excluded.
        self.assertFalse(is_in_flight(bead("SpatialEngine-old.1",
                                           status="closed",
                                           labels=["in-flight"])))

    def test_a_bead_labelled_in_flight_is_in_flight_whatever_its_status(self):
        self.assertTrue(is_in_flight(bead("SpatialEngine-q.1",
                                          labels=["in-flight"])))
        self.assertTrue(is_in_flight(bead("SpatialEngine-q.2",
                                          status="in_progress")))
        self.assertFalse(is_in_flight(bead("SpatialEngine-q.3")))

    def test_two_in_flight_beads_overlapping_is_reported_once_per_candidate(self):
        ready = [bead("SpatialEngine-x.2", description="eng/verify.sh")]
        flying = [bead("SpatialEngine-x.1", status="in_progress",
                       description="eng/verify.sh"),
                  bead("SpatialEngine-x.3", status="in_progress",
                       description="eng/verify.sh")]
        verdict = plan(ready, flying)[0]
        self.assertEqual(
            sorted(verdict.blocked_by), ["SpatialEngine-x.1",
                                             "SpatialEngine-x.3"])

    def test_a_fileset_is_the_bead_s_own_title_and_description(self):
        self.assertEqual(
            bead_files(bead("SpatialEngine-x.1", "edit tools/verify_scope.py",
                            "and eng/verify.sh")),
            ["eng/verify.sh", "tools/verify_scope.py"])


class ReadListTests(unittest.TestCase):
    """Principle 1 with files instead of messages: the resolved read list."""

    def setUp(self):
        self.rows = parse_routing(routing_markdown())

    def test_an_adr_the_bead_cites_leads_the_list(self):
        listed = read_list(
            bead("SpatialEngine-x.1", "tighten the gate",
                 "behaves as ADR-0152 says, and amends ADR-0134"),
            self.rows)
        self.assertEqual(listed[0].adrs, ["ADR-0152", "ADR-0134"])
        self.assertEqual(listed[0].source, "cited by the bead")

    def test_a_bead_over_an_adapter_routes_to_that_row_and_names_it(self):
        listed = read_list(
            bead("SpatialEngine-x.1", "Esri GeoServices REST relation patterns",
                 "in src/Spatial.Adapter.GeoServices/RelationMatrix.cs"),
            self.rows)
        matched = [row for row in listed if row.source == "architecture/distilled/README.md"]
        self.assertTrue(any("Esri GeoServices REST" in row.task for row in matched))
        esri = next(row for row in matched if "Esri GeoServices REST" in row.task)
        self.assertEqual(esri.adrs, ["ADR-0035", "ADR-0037", "ADR-0048",
                                     "ADR-0112"])
        self.assertIn("host-and-clients.md", esri.reads)
        self.assertTrue(esri.matched)

    def test_a_range_in_the_routing_table_is_expanded(self):
        row = next(row for row in self.rows if row.task.startswith("HTTP API"))
        self.assertEqual(row.adrs, [f"ADR-{n:04d}" for n in range(14, 20)]
                         + ["ADR-0033"])

    def test_a_row_with_no_adrs_carries_none(self):
        row = next(row for row in self.rows
                   if row.task.startswith("Any architectural change"))
        self.assertEqual(row.adrs, [])

    def test_the_repository_routing_table_parses(self):
        rows = parse_routing(DISTILLED_README.read_text(encoding="utf-8"))
        self.assertGreater(len(rows), 5)
        self.assertTrue(any(row.task.startswith("Core geometry")
                            for row in rows))
        self.assertTrue(any("ADR-0001" in row.adrs for row in rows))


class MetricsTests(unittest.TestCase):
    """The four incidents the bead says to count, before and after."""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = make_repo(Path(self._tmp.name))
        self.addCleanup(self._tmp.cleanup)

    def test_a_wip_preservation_commit_is_counted(self):
        commit(self.root, "WIP: preserve uncommitted work for SpatialEngine-x.1 "
                          "(swarm capped at 3 by coordinator tick 2026-09-28)")
        counts = metrics(self.root, since=None)["wip_preservation"]
        self.assertEqual(counts, 1)

    def test_an_ordinary_commit_is_not_counted(self):
        commit(self.root, "an ordinary commit")
        self.assertEqual(metrics(self.root, since=None)["wip_preservation"], 0)

    def test_a_merge_whose_work_names_another_bead_is_a_mis_dispatch(self):
        base = git(self.root, "rev-parse", "HEAD")
        git(self.root, "checkout", "-q", "-b", "bd/SpatialEngine-x.4")
        commit(self.root, "work for SpatialEngine-x.2", "Task: SpatialEngine-x.2")
        git(self.root, "checkout", "-q", "main")
        git(self.root, "merge", "-q", "--no-ff", "-m",
            "Merge SpatialEngine-x.4: the bead's own work", "bd/SpatialEngine-x.4")
        result = metrics(self.root, since=base)
        self.assertEqual(result["mis_dispatch"], ["SpatialEngine-x.4"])

    def test_a_merge_whose_work_names_its_own_bead_is_not_a_mis_dispatch(self):
        base = git(self.root, "rev-parse", "HEAD")
        git(self.root, "checkout", "-q", "-b", "bd/SpatialEngine-x.4")
        commit(self.root, "work for SpatialEngine-x.4", "Task: SpatialEngine-x.4")
        git(self.root, "checkout", "-q", "main")
        git(self.root, "merge", "-q", "--no-ff", "-m",
            "Merge SpatialEngine-x.4: the bead's own work", "bd/SpatialEngine-x.4")
        self.assertEqual(metrics(self.root, since=base)["mis_dispatch"], [])

    def test_a_merge_with_no_trailers_is_not_a_mis_dispatch(self):
        # A missing trailer is beads_gate's finding; calling it a mis-dispatch
        # here would double-count one incident as another.
        base = git(self.root, "rev-parse", "HEAD")
        git(self.root, "checkout", "-q", "-b", "bd/SpatialEngine-x.4")
        commit(self.root, "work with no trailer")
        git(self.root, "checkout", "-q", "main")
        git(self.root, "merge", "-q", "--no-ff", "-m",
            "Merge SpatialEngine-x.4: no trailer anywhere", "bd/SpatialEngine-x.4")
        self.assertEqual(metrics(self.root, since=base)["mis_dispatch"], [])

    def test_the_counts_come_back_as_a_named_dict_over_the_range(self):
        commit(self.root, "WIP: preserve uncommitted work for SpatialEngine-x.9")
        counts = metrics(self.root, since=None)
        self.assertEqual(counts["range"], "all history")
        self.assertEqual(counts["wip_preservation"], 1)


class MainTests(unittest.TestCase):
    """The command the runbook calls, over a bead list rather than the queue."""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        self.beads_file = self.root / "beads.json"
        self.addCleanup(self._tmp.cleanup)

    def _write(self, beads):
        self.beads_file.write_text(json.dumps(beads), encoding="utf-8")

    def test_the_named_bead_gate_exits_non_zero_when_it_is_blocked(self):
        self._write([
            bead("SpatialEngine-x.1", status="in_progress",
                 description="tools/verify_scope.py"),
            bead("SpatialEngine-x.2", description="tools/verify_scope.py"),
        ])
        code = main(["--beads", str(self.beads_file), "--root", str(self.root),
                     "--bead", "SpatialEngine-x.2", "--json"], out=io.StringIO())
        self.assertEqual(code, 1)

    def test_the_named_bead_gate_exits_zero_when_it_is_free(self):
        self._write([
            bead("SpatialEngine-x.1", status="in_progress",
                 description="tools/verify_scope.py"),
            bead("SpatialEngine-x.2", description="eng/verify.sh"),
        ])
        code = main(["--beads", str(self.beads_file), "--root", str(self.root),
                     "--bead", "SpatialEngine-x.2", "--json"], out=io.StringIO())
        self.assertEqual(code, 0)

    def test_json_output_carries_the_verdicts(self):
        self._write([
            bead("SpatialEngine-x.1", status="in_progress",
                 description="tools/verify_scope.py"),
            bead("SpatialEngine-x.2", description="tools/verify_scope.py"),
        ])
        buffer = io.StringIO()
        main(["--beads", str(self.beads_file), "--root", str(self.root),
              "--json"], out=buffer)
        payload = json.loads(buffer.getvalue())
        self.assertEqual(payload["verdicts"][0]["id"], "SpatialEngine-x.2")
        self.assertEqual(payload["verdicts"][0]["blocked_by"],
                         ["SpatialEngine-x.1"])

    def test_the_read_mode_prints_the_resolved_list_for_a_named_bead(self):
        self._write([bead("SpatialEngine-x.1", "tighten the gate",
                          "as ADR-0152 says")])
        buffer = io.StringIO()
        code = main(["read", "SpatialEngine-x.1", "--beads",
                     str(self.beads_file), "--root", str(self.root)],
                    out=buffer)
        self.assertEqual(code, 0)
        self.assertIn("ADR-0152", buffer.getvalue())


class RunbookTests(unittest.TestCase):
    """The runbook's own text: the ordering is derived, not hand-listed."""

    def setUp(self):
        self.text = RUNBOOK.read_text(encoding="utf-8")

    def test_the_hand_listed_pair_order_is_gone(self):
        # The clause this bead exists to remove. It came back the first time
        # one new overlapping bead landed, because a hand list does not notice
        # the ones it has never heard of.
        self.assertNotIn("before `.16`", self.text)
        self.assertNotIn("before `.21`", self.text)

    def test_the_drain_step_calls_the_derived_lock(self):
        self.assertIn("tools/swarm_lock.py", self.text)
        self.assertIn("--bead <id>", self.text)

    def test_the_worker_prompt_carries_the_resolved_read_list(self):
        self.assertIn("swarm_lock.py read", self.text)

    def test_the_cap_is_bounded_at_four_until_the_lock_has_held_a_drain(self):
        self.assertIn("ceiling of 4", self.text)

    def test_the_incidents_are_measured_per_drain_cycle(self):
        self.assertIn("swarm_lock.py metrics", self.text)


if __name__ == "__main__":
    unittest.main()