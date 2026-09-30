#!/usr/bin/env python3
"""Tests for tools/bd-safe-reclaim.py.

Run: python3 -m unittest tools/test_bd_safe_reclaim.py

`bd reclaim` keys on lease age alone, and a long-running worker does not
heartbeat its lease, so reclaim released eight live leases in one coordinator
tick (SpatialEngine-u2x.30). The recovery step is the only coordinator step
that can destroy in-flight state, so the safe wrapper has to prove that a
lease which is expired but whose paseo agent is alive is left in place.
"""
import importlib.util
import io
import json
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

SCRIPT = Path(__file__).with_name("bd-safe-reclaim.py")

NOW = datetime(2026, 9, 28, 12, 0, 0, tzinfo=timezone.utc)


def load_script():
    """Import the dashed script filename as a module."""
    spec = importlib.util.spec_from_file_location("bd_safe_reclaim", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def agent(agent_id, status="running",
          cwd="~/.paseo/worktrees/1mmcart7/bd-spatialengine-u2x-21"):
    return {"id": agent_id, "shortId": agent_id[:6], "name": "worker",
            "provider": "pi/opencode-go/space-bunny-free",
            "thinking": "medium", "status": status, "cwd": cwd, "created": ""}


def workspace(name, cwd):
    return {"workspaceId": "wks_x", "project": "SpatialEngine", "name": name,
            "isolation": "worktree", "cwd": cwd}


def bead(bead_id, *, lease_minutes_ago, heartbeat_minutes_ago=None, notes="",
         status="in_progress"):
    expired = NOW - timedelta(minutes=lease_minutes_ago)
    beat = NOW - timedelta(
        minutes=lease_minutes_ago if heartbeat_minutes_ago is None
        else heartbeat_minutes_ago)
    return {"id": bead_id, "title": "work", "status": status,
            "lease_expires_at": expired.isoformat().replace("+00:00", "Z"),
            "heartbeat_at": beat.isoformat().replace("+00:00", "Z"),
            "notes": notes}


class StalenessTests(unittest.TestCase):
    """The grace window is the one thing `bd reclaim` gets right; keep it."""

    def setUp(self):
        self.module = load_script()

    def test_expired_lease_is_stale_past_the_grace_window(self):
        self.assertTrue(self.module.is_stale(
            bead("SpatialEngine-u2x.9", lease_minutes_ago=30),
            now=NOW, older_than=timedelta(minutes=10)))

    def test_lease_inside_the_grace_window_is_left_alone(self):
        self.assertFalse(self.module.is_stale(
            bead("SpatialEngine-u2x.9", lease_minutes_ago=2),
            now=NOW, older_than=timedelta(minutes=10)))

    def test_unexpired_lease_is_never_stale(self):
        self.assertFalse(self.module.is_stale(
            {"id": "x", "lease_expires_at":
             (NOW + timedelta(minutes=5)).isoformat().replace("+00:00", "Z")},
            now=NOW, older_than=timedelta(minutes=10)))

    def test_missing_lease_is_never_stale(self):
        # A bead that was never claimed has no lease to reap.
        self.assertFalse(self.module.is_stale(
            {"id": "x"}, now=NOW, older_than=timedelta(minutes=0)))


class RunningAgentTests(unittest.TestCase):
    """The incident: expired heartbeat, agent alive. Must not be reclaimed."""

    def setUp(self):
        self.module = load_script()
        self.agents = [
            agent("17f1626aaaa", cwd="~/.paseo/worktrees/1mmcart7/"
                 "bd-spatialengine-u2x-21"),
            agent("b67f9bfbbbb", status="idle",
                  cwd="~/Work/SpatialEngine"),
        ]
        self.workspaces = [
            workspace("bd/SpatialEngine-u2x.21",
                      "~/.paseo/worktrees/1mmcart7/bd-spatialengine-u2x-21"),
            workspace("Coordinate SpatialEngine-u2x swarm run",
                      "~/Work/SpatialEngine"),
        ]

    def test_expired_lease_with_running_agent_is_reclaimed_by_bare_reclaim(self):
        # The bug, pinned as the behaviour the wrapper has to change: age
        # alone says "dead" even though paseo says "running".
        target = bead("SpatialEngine-u2x.21", lease_minutes_ago=24,
                      heartbeat_minutes_ago=24)
        self.assertTrue(self.module.is_stale(
            target, now=NOW, older_than=timedelta(minutes=10)))
        self.assertIsNotNone(self.module.protect_reason(
            target, self.agents, self.workspaces))

    def test_plan_keeps_the_live_lease(self):
        live = bead("SpatialEngine-u2x.21", lease_minutes_ago=24)
        decisions = self.module.plan(
            [live], self.agents, self.workspaces, now=NOW,
            older_than=timedelta(minutes=10))
        self.assertEqual([d.bead_id for d in decisions], ["SpatialEngine-u2x.21"])
        self.assertEqual([d.reclaim for d in decisions], [False])
        self.assertIn("17f1626aaaa", decisions[0].reason)

    def test_idle_agent_still_holds_the_lease(self):
        idle = agent("b67f9bfbbbb", status="idle",
                     cwd="~/.paseo/worktrees/1mmcart7/bd-spatialengine-u2x-9")
        target = bead("SpatialEngine-u2x.9", lease_minutes_ago=90)
        self.assertIsNotNone(self.module.protect_reason(
            target, [idle], [workspace(
                "bd/SpatialEngine-u2x.9",
                "~/.paseo/worktrees/1mmcart7/bd-spatialengine-u2x-9")]))

    def test_closed_agent_does_not_hold_the_lease(self):
        gone = agent("b67f9bfbbbb", status="closed",
                     cwd="~/.paseo/worktrees/1mmcart7/bd-spatialengine-u2x-9")
        self.assertIsNone(self.module.protect_reason(
            bead("SpatialEngine-u2x.9", lease_minutes_ago=90), [gone],
            self.workspaces))

    def test_agent_id_recorded_in_the_notes_protects_the_lease(self):
        # Recovery is reversible only if the claim recorded which agent took
        # the bead, so the notes' agent id is a liveness signal in its own
        # right — the worktree may already be gone.
        target = bead("SpatialEngine-u2x.7", lease_minutes_ago=45,
                      notes="agent 17f1626aaaa on branch bd/SpatialEngine-u2x.7")
        self.assertIsNotNone(self.module.protect_reason(
            target, self.agents, []))

    def test_unknown_agent_id_in_the_notes_protects_nothing(self):
        target = bead("SpatialEngine-u2x.7", lease_minutes_ago=45,
                      notes="agent deadbeefcafe claimed it")
        self.assertIsNone(self.module.protect_reason(target, self.agents, []))

    def test_prefix_bead_ids_do_not_share_a_worktree(self):
        # `SpatialEngine-u2x.2` must not be protected by a live agent on
        # `bd-spatialengine-u2x-21`.
        target = bead("SpatialEngine-u2x.2", lease_minutes_ago=90)
        self.assertIsNone(self.module.protect_reason(
            target, self.agents, self.workspaces))

    def test_coordinator_on_the_repo_root_protects_nothing(self):
        target = bead("SpatialEngine-u2x.2", lease_minutes_ago=90)
        self.assertIsNone(self.module.protect_reason(
            target, self.agents, self.workspaces))

    def test_dead_worker_with_no_agent_is_reclaimed(self):
        target = bead("SpatialEngine-fhf", lease_minutes_ago=90)
        decisions = self.module.plan(
            [target], self.agents, self.workspaces, now=NOW,
            older_than=timedelta(minutes=10))
        self.assertEqual([d.reclaim for d in decisions], [True])


class ReclaimCommandTests(unittest.TestCase):
    """Only the planned ids reach `bd reclaim`, and never in a dry run."""

    def setUp(self):
        self.module = load_script()
        self.calls = []

    def run_main(self, argv, beads, agents, workspaces):
        # The report goes to a buffer, not to the gate's stdout: a fixture
        # recovery that prints 'reclaimed 1, kept 1' into a verify run is
        # indistinguishable from a real one, and a coordinator read exactly
        # that line as a live recovery on 2026-09-28 (SpatialEngine-k0p).
        self.out = io.StringIO()

        def fake_run(cmd, **kwargs):
            self.calls.append(cmd)
            if cmd[0] == "bd" and cmd[1] == "list":
                return self.module.Completed(0, json.dumps(beads), "")
            if cmd[0] == "paseo" and cmd[1] == "ls":
                return self.module.Completed(0, json.dumps(agents), "")
            if cmd[0] == "paseo" and cmd[1] == "workspace":
                return self.module.Completed(
                    0, json.dumps(workspaces), "")
            return self.module.Completed(0, "ok", "")

        return self.module.main(
            argv, run=fake_run, now=NOW,
            older_than=timedelta(minutes=10), out=self.out)

    def test_eight_live_workers_release_nothing(self):
        agents = [agent(f"{i:06x}aaaa", cwd=f"~/.paseo/worktrees/t/"
                        f"bd-spatialengine-{name}")
                  for i, name in enumerate(
                      ["u2x-9", "u2x-21", "fhf", "0zp", "u2x-26", "u2x-30"])]
        beads = [bead(f"SpatialEngine-{name}", lease_minutes_ago=30)
                 for name in ["u2x.9", "u2x.21", "fhf", "0zp", "u2x.26",
                              "u2x.30"]]
        code = self.run_main([], beads, agents, [])
        self.assertEqual(code, 0)
        self.assertEqual([c for c in self.calls if c[1] == "reclaim"], [])

    def test_reclaim_names_only_the_dead_bead(self):
        agents = [agent("17f1626aaaa",
                        cwd="~/.paseo/worktrees/t/bd-spatialengine-u2x-21")]
        beads = [bead("SpatialEngine-u2x.21", lease_minutes_ago=30),
                 bead("SpatialEngine-fhf", lease_minutes_ago=30)]
        code = self.run_main([], beads, agents, [])
        self.assertEqual(code, 0)
        reclaims = [c for c in self.calls if c[1] == "reclaim"]
        self.assertEqual(len(reclaims), 1)
        self.assertEqual(reclaims[0][:3], ["bd", "reclaim", "--id"])
        self.assertEqual(reclaims[0][3], "SpatialEngine-fhf")

    def test_dry_run_never_calls_bd_reclaim(self):
        beads = [bead("SpatialEngine-fhf", lease_minutes_ago=30)]
        self.assertEqual(self.run_main(["--dry-run"], beads, [], []), 0)
        self.assertEqual([c for c in self.calls if c[1] == "reclaim"], [])

    def test_reclaim_failure_is_reported(self):
        module = self.module

        class Failing:
            def __init__(self, inner):
                self.inner = inner

            def __call__(self, cmd, **kwargs):
                if cmd[1] == "reclaim":
                    return module.Completed(1, "", "boom")
                return self.inner(cmd, **kwargs)

        inner_calls = self.calls

        def base_run(cmd, **kwargs):
            inner_calls.append(cmd)
            if cmd[1] == "list":
                return module.Completed(0, '[{"id": "SpatialEngine-fhf", '
                                     '"lease_expires_at": "2026-09-28T11:00:00Z"}]', "")
            if cmd[1] == "ls":
                return module.Completed(0, "[]", "")
            if cmd[1] == "workspace":
                return module.Completed(0, "[]", "")
            return module.Completed(0, "", "")

        code = module.main(
            [], run=Failing(base_run), now=NOW,
            older_than=timedelta(minutes=0), out=io.StringIO())
        self.assertEqual(code, 1)


class QueueOverrideTests(unittest.TestCase):
    """`--db` names the queue, so no run of this tool has to be the real one.

    The wrapper reclaims leases in whatever queue `bd` resolves from the cwd,
    which for this repository is the shared `.beads` database every worktree
    sees. A test that forgot to stub the CLI, or a human rehearsing a recovery,
    would then be writing to the live queue. `--db` gives both a scratch queue
    to point at, and the property is checkable: every `bd` call the tool makes
    carries the override, including the reclaim itself (SpatialEngine-k0p).
    """

    def setUp(self):
        self.module = load_script()
        self.calls = []

    def run_main(self, argv, beads, agents=(), workspaces=()):
        module = self.module

        def fake_run(cmd, **kwargs):
            self.calls.append(cmd)
            if cmd[1] == "list":
                return module.Completed(
                    0, json.dumps(list(beads)), "")
            if cmd[1] == "ls":
                return module.Completed(0, json.dumps(list(agents)), "")
            if cmd[1] == "workspace":
                return module.Completed(
                    0, json.dumps(list(workspaces)), "")
            return module.Completed(0, "", "")

        out = io.StringIO()
        return module.main(argv, run=fake_run, now=NOW,
                           older_than=timedelta(minutes=10), out=out), out

    def test_every_bd_call_carries_the_queue_override(self):
        scratch = "/tmp/fixture-queue/beads.db"
        code, _ = self.run_main(
            ["--db", scratch], [bead("SpatialEngine-fhf", lease_minutes_ago=30)])
        self.assertEqual(code, 0)
        bd_calls = [c for c in self.calls if c[0] == "bd"]
        self.assertTrue(bd_calls)
        for call in bd_calls:
            self.assertIn("--db", call, call)
            self.assertEqual(call[call.index("--db") + 1], scratch, call)

    def test_the_reclaim_itself_is_against_the_override_queue(self):
        scratch = "/tmp/fixture-queue/beads.db"
        self.run_main(["--db", scratch],
                      [bead("SpatialEngine-fhf", lease_minutes_ago=30)])
        reclaim = [c for c in self.calls if c[1] == "reclaim"]
        self.assertEqual(len(reclaim), 1)
        self.assertEqual(reclaim[0], ["bd", "reclaim", "--id",
                                      "SpatialEngine-fhf", "--db", scratch])

    def test_the_heartbeat_turn_is_against_the_override_queue_too(self):
        code = self.module.main(
            ["--db", "/tmp/fixture-queue/beads.db",
             "--heartbeat", "SpatialEngine-k0p"],
            run=lambda cmd, **kwargs: (self.calls.append(cmd),
                                       self.module.Completed(0, "", ""))[1])
        self.assertEqual(code, 0)
        self.assertEqual(self.calls, [["bd", "heartbeat", "SpatialEngine-k0p",
                                       "--db", "/tmp/fixture-queue/beads.db"]])

    def test_without_the_override_the_commands_are_unchanged(self):
        # The runbook's command must keep meaning what it says: no flag added,
        # so the queue is whatever `bd` resolves, which is the real one.
        code, _ = self.run_main(
            [], [bead("SpatialEngine-fhf", lease_minutes_ago=30)])
        self.assertEqual(code, 0)
        for call in [c for c in self.calls if c[0] == "bd"]:
            self.assertNotIn("--db", call)


class HeartbeatTests(unittest.TestCase):
    """A worker can also keep its own lease alive through the same tool."""

    def setUp(self):
        self.module = load_script()
        self.calls = []

    def test_heartbeat_ticks_the_named_bead(self):
        def fake_run(cmd, **kwargs):
            self.calls.append(cmd)
            return self.module.Completed(0, "", "")

        code = self.module.main(["--heartbeat", "SpatialEngine-u2x.30"],
                                run=fake_run, now=NOW)
        self.assertEqual(code, 0)
        self.assertEqual(self.calls, [["bd", "heartbeat", "SpatialEngine-u2x.30"]])


if __name__ == "__main__":
    unittest.main()
