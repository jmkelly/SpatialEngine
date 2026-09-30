#!/usr/bin/env python3
"""Tests for tools/bd-merge-bead.py.

Run: python3 -m unittest tools/test_bd_merge_bead.py

The swarm was closing beads whose work had never reached origin/main, and
merging into a local main it then never pushed (SpatialEngine-xbz). Two
distinct losses, and both of them are mechanical once the merge is a tool
rather than a paragraph:

  * `bd close` was called on the strength of a green eng/verify.sh on the
    BRANCH, so a bead went closed while its commit sat on `bd/<id>`.
  * a merge landed in local main and was never pushed, so `origin/main` — the
    base every new worktree is branched off — stayed behind, and a later tick
    would re-merge work that was already merged.

So the tool is the gate: `bd close` is the last thing it does, and only after
`git merge-base --is-ancestor <merge> origin/main` has passed on a freshly
fetched origin. The tests drive it through a scripted git so the ordering is
pinned, not just the happy path.
"""
import importlib.util
import json
import re
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("bd-merge-bead.py")


def load_script():
    """Import the dashed script filename as a module."""
    spec = importlib.util.spec_from_file_location("bd_merge_bead", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class FakeGit:
    """Just enough git to see which ref a commit ended up on.

    Each ref is the ordered list of commit shas it contains. `push` moves the
    remote ref, unless the test made that push fail (the unpushed-merge bug).
    """

    def __init__(self, main=(), origin_main=(), push_fails=False,
                 rebase_fails=False):
        self.refs = {"main": list(main), "origin/main": list(origin_main)}
        self.push_fails = push_fails
        self.rebase_fails = rebase_fails
        self.worktrees = {}
        self.calls = []
        self.known = set()
        self.equivalent = {}

    def know(self, sha, equivalent_to=None):
        """A commit this repo has, optionally under a different sha on main."""
        self.known.add(sha)
        if equivalent_to:
            self.equivalent[sha] = equivalent_to

    def worktree(self, branch, path):
        self.worktrees[branch] = path

    def branch(self, name, commits):
        self.refs[name] = list(commits)
        return self.refs[name]

    def push(self, remote_ref, local_ref):
        # A non-fast-forward push is what a stale origin looks like to git. A
        # remote ref that does not exist yet is a create, not a rejection.
        remote = self.refs.get(remote_ref, [])
        if any(c not in self.refs[local_ref] for c in remote):
            raise PushRejected(f"! [rejected] {local_ref} -> {remote_ref} "
                               "(non-fast-forward)")
        self.refs[remote_ref] = list(self.refs[local_ref])

    def rebase(self, onto, branch):
        base = self.refs[onto]
        extra = [c for c in self.refs[branch] if c not in base]
        self.refs[branch] = base + extra

    def merge(self, branch, message=None):
        extra = [c for c in self.refs[branch] if c not in self.refs["main"]]
        self.refs["main"] += extra
        return self.refs["main"][-1] if extra else self.refs["main"][-1]

    def ahead(self, local, remote):
        return [c for c in self.refs[local] if c not in self.refs[remote]]

    def is_ancestor(self, sha, ref):
        return sha in self.refs[ref]


class PushRejected(Exception):
    """The remote has commits the local ref does not: git says non-fast-forward."""


class Harness:
    """A `run` callable over a FakeGit, plus the calls it recorded."""

    def __init__(self, git, module, verify_ok=True, beads=None,
                 verify_cancels=False):
        self.git = git
        self.module = module
        self.verify_ok = verify_ok
        self.verify_cancels = verify_cancels
        self.beads = beads or []
        self.lines = []
        self.cwds = []

    def __call__(self, cmd, **kwargs):
        self.git.calls.append(list(cmd))
        self.cwds.append((cmd[0], cmd[1] if len(cmd) > 1 else "", kwargs.get("cwd")))
        head = cmd[1] if cmd[0] == "git" else cmd[0]
        if head == "worktree":
            text = "".join(
                f"worktree {path}\nHEAD abc\nbranch refs/heads/{branch}\n\n"
                for branch, path in self.git.worktrees.items())
            return self.module.Completed(0, text, "")
        if head == "fetch":
            return self.module.Completed(0, "", "")
        if head == "log":
            if ".." not in cmd[-1]:
                return self.module.Completed(0, "a subject\n", "")
            base, _, ref = cmd[-1].partition("..")
            commits = self.git.ahead(ref, base)
            return self.module.Completed(
                0, "".join(f"{c} subject {c}\n" for c in commits), "")
        if head == "merge-base":
            ok = self.git.is_ancestor(cmd[3], cmd[4])
            return self.module.Completed(0 if ok else 1, "", "")
        if head == "rev-parse":
            name = cmd[-1]
            ref = name.split("^{")[0]
            if ref in self.git.refs:
                return self.module.Completed(0, self.git.refs[ref][-1] + "\n", "")
            if ref in self.git.known:
                return self.module.Completed(0, ref + "\n", "")
            return self.module.Completed(1, "", f"unknown ref {name}")
        if head == "cherry":
            sha = cmd[-1]
            if sha not in self.git.known:
                return self.module.Completed(1, "", f"unknown commit {sha}")
            if sha in self.git.equivalent:
                mark = self.git.equivalent[sha]
            else:
                mark = sha
            return self.module.Completed(0, f"{mark} subject {sha}\n", "")
        if head == "rebase":
            if self.git.rebase_fails:
                return self.module.Completed(1, "", "conflict")
            self.git.rebase(cmd[2], cmd[3])
            return self.module.Completed(0, "", "")
        if head == "merge":
            sha = self.git.merge(cmd[-1])
            return self.module.Completed(0, f"Merge {sha}\n", "")
        if head == "push":
            if self.git.push_fails:
                return self.module.Completed(
                    1, "", "! [rejected] main -> main (non-fast-forward)")
            local = cmd[cmd.index("origin") + 1]
            if local not in self.git.refs:
                return self.module.Completed(0, "", "")
            try:
                self.git.push(f"origin/{local}", local)
            except PushRejected as rejected:
                return self.module.Completed(1, "", str(rejected))
            return self.module.Completed(0, "", "")
        if head == "verify.sh" or cmd[0].endswith("verify.sh"):
            if self.verify_cancels:
                raise KeyboardInterrupt()
            return self.module.Completed(
                0 if self.verify_ok else 1, "", "tests failed" if not self.verify_ok else "")
        if head == "bd" and cmd[1] == "close":
            return self.module.Completed(0, "", "")
        if head == "bd" and cmd[1] == "note":
            return self.module.Completed(0, "", "")
        if head == "bd" and cmd[1] == "list":
            return self.module.Completed(0, json.dumps(self.beads), "")
        return self.module.Completed(0, "", "")

    def emit(self, line):
        self.lines.append(line)

    def write(self, text):
        """So the harness can stand in for the stream main() prints to."""
        self.lines.append(text.rstrip("\n"))

    def called(self, *prefix):
        return [c for c in self.git.calls
                if tuple(c[:len(prefix)]) == prefix]

    def verify_calls(self):
        return [c for c in self.git.calls
                if c and str(c[0]).endswith("verify.sh")]

    def closed(self):
        return [c[2] for c in self.called("bd", "close")]


class MergeFlowTests(unittest.TestCase):
    """The merge path: fetch, rebase, verify, merge, push, then close."""

    def setUp(self):
        self.module = load_script()

    def run_tool(self, argv, git, **kwargs):
        harness = Harness(git, self.module, **kwargs)
        code = self.module.main(argv, run=harness, out=harness)
        return code, harness

    def test_close_happens_only_after_origin_main_carries_the_merge(self):
        git = FakeGit(main=["a1"], origin_main=["a1"])
        git.branch("bd/SpatialEngine-xbz", ["a1", "w1"])
        code, harness = self.run_tool(["--bead", "SpatialEngine-xbz"], git)
        self.assertEqual(code, 0)
        self.assertEqual(harness.closed(), ["SpatialEngine-xbz"])
        order = [c[:2] for c in harness.git.calls]
        self.assertIn(["git", "fetch"], order)
        self.assertLess(order.index(["git", "push"]), order.index(["bd", "close"]))
        self.assertLess(
            order.index(["git", "merge-base"]),
            order.index(["bd", "close"]))

    def test_failed_push_leaves_the_bead_open(self):
        # The incident: the merge was correct and was never pushed.
        git = FakeGit(main=["a1"], origin_main=["a1"], push_fails=True)
        git.branch("bd/SpatialEngine-xbz", ["a1", "w1"])
        code, harness = self.run_tool(["--bead", "SpatialEngine-xbz"], git)
        self.assertEqual(code, 1)
        self.assertEqual(harness.closed(), [])
        self.assertIn("SpatialEngine-xbz", " ".join(harness.lines))

    def test_merge_that_is_not_on_origin_main_never_closes(self):
        # Even if the push "succeeded" by the tool's own reckoning, the close
        # gate is the ancestor check against origin/main, not local main.
        class Diverged(FakeGit):
            def push(self, remote_ref, local_ref):
                pass  # the remote ref never moves
        git = Diverged(main=["a1"], origin_main=["a1"])
        git.branch("bd/SpatialEngine-xbz", ["a1", "w1"])
        code, harness = self.run_tool(["--bead", "SpatialEngine-xbz"], git)
        self.assertEqual(code, 1)
        self.assertEqual(harness.closed(), [])

    def test_red_verify_aborts_before_any_merge(self):
        git = FakeGit(main=["a1"], origin_main=["a1"])
        git.branch("bd/SpatialEngine-xbz", ["a1", "w1"])
        code, harness = self.run_tool(
            ["--bead", "SpatialEngine-xbz"], git, verify_ok=False)
        self.assertEqual(code, 1)
        self.assertEqual(harness.closed(), [])
        self.assertEqual(harness.called("git", "merge"), [])
        self.assertEqual(harness.git.refs["main"], ["a1"])

    def test_branch_is_rebased_onto_origin_main(self):
        # origin/main has moved since this branch was cut; the rebase has to
        # land on origin/main, or the merge re-does work that is already in.
        git = FakeGit(main=["a1"], origin_main=["a1", "o9"])
        git.branch("bd/SpatialEngine-xbz", ["a1", "w1"])
        self.run_tool(["--bead", "SpatialEngine-xbz"], git)
        self.assertEqual(git.refs["bd/SpatialEngine-xbz"], ["a1", "o9", "w1"])
        self.assertEqual(git.refs["main"], ["a1", "o9", "w1"])

    def test_rebase_and_verify_run_in_the_beads_own_worktree(self):
        # git refuses to rebase a branch checked out in another worktree, so
        # doing this in the repo root is how a merge step dies half-way.
        git = FakeGit(main=["a1"], origin_main=["a1"])
        git.branch("bd/SpatialEngine-xbz", ["a1", "w1"])
        git.worktree("bd/SpatialEngine-xbz", "/wt/xbz")
        code, harness = self.run_tool(["--bead", "SpatialEngine-xbz"], git)
        self.assertEqual(code, 0)
        for head, sub, where in harness.cwds:
            if sub in ("rebase",) or head.endswith("verify.sh"):
                self.assertEqual(where, "/wt/xbz")
            if sub == "merge":
                self.assertNotEqual(where, "/wt/xbz")

    def test_refuses_to_merge_while_local_main_is_unpublished(self):
        # A local main that origin does not have is the state the incident
        # left behind; merging into it is how the queue drifts. Publish first.
        git = FakeGit(main=["a1", "m9"], origin_main=["a1"])
        git.branch("bd/SpatialEngine-xbz", ["a1", "w1"])
        code, harness = self.run_tool(["--bead", "SpatialEngine-xbz"], git)
        self.assertEqual(code, 1)
        self.assertEqual(harness.closed(), [])
        self.assertEqual(harness.called("git", "merge"), [])
        self.assertTrue(any("--publish" in line for line in harness.lines))


class PublishCheckTests(unittest.TestCase):
    """`--check` is the top-of-tick gate: is origin/main caught up?"""

    def setUp(self):
        self.module = load_script()

    def run_tool(self, argv, git, **kwargs):
        harness = Harness(git, self.module, **kwargs)
        return self.module.main(argv, run=harness, out=harness), harness

    def test_unpublished_main_fails(self):
        git = FakeGit(main=["a1", "m9"], origin_main=["a1"])
        code, harness = self.run_tool(["--check"], git)
        self.assertEqual(code, 1)
        self.assertIn("m9", " ".join(harness.lines))

    def test_published_main_passes(self):
        git = FakeGit(main=["a1", "m9"], origin_main=["a1", "m9"])
        code, _ = self.run_tool(["--check"], git)
        self.assertEqual(code, 0)

    def test_publish_pushes_local_main_and_passes_the_check_after(self):
        git = FakeGit(main=["a1", "m9"], origin_main=["a1"])
        code, harness = self.run_tool(["--publish"], git)
        self.assertEqual(code, 0)
        self.assertEqual(git.refs["origin/main"], ["a1", "m9"])

    def test_publish_does_not_force_without_the_flag(self):
        git = FakeGit(main=["a1", "m9"], origin_main=["a1", "other"])
        code, harness = self.run_tool(["--publish"], git)
        self.assertEqual(code, 1)
        self.assertEqual(harness.called("git", "push", "--force-with-lease"), [])


class AuditTests(unittest.TestCase):
    """`--audit` finds the beads this class of bug already created."""

    def setUp(self):
        self.module = load_script()

    def run_tool(self, argv, git, **kwargs):
        harness = Harness(git, self.module, **kwargs)
        return self.module.main(argv, run=harness, out=harness), harness

    def bead(self, bead_id, status, notes=""):
        return {"id": bead_id, "status": status, "notes": notes}

    def test_closed_bead_whose_commit_is_not_published_is_reported(self):
        git = FakeGit(main=["a1"], origin_main=["a1"])
        git.know("4d833a2")
        code, harness = self.run_tool(
            ["--audit"],
            git,
            beads=[self.bead("SpatialEngine-u2x.11", "closed",
                             "merged as 4d833a2 on bd/SpatialEngine-u2x.11")])
        self.assertEqual(code, 1)
        self.assertIn("SpatialEngine-u2x.11", " ".join(harness.lines))

    def test_published_closed_beads_pass(self):
        git = FakeGit(main=["a1", "w1"], origin_main=["a1", "w1"])
        code, _ = self.run_tool(
            ["--audit"],
            git,
            beads=[self.bead("SpatialEngine-u2x.11", "closed",
                             "merged as w1 on bd/SpatialEngine-u2x.11")])
        self.assertEqual(code, 0)

    def test_open_beads_are_not_audited(self):
        git = FakeGit(main=["a1"], origin_main=["a1"])
        code, _ = self.run_tool(
            ["--audit"],
            git,
            beads=[self.bead("SpatialEngine-xbz", "in_progress",
                             "wip w1 on bd/SpatialEngine-xbz")])
        self.assertEqual(code, 0)

    def test_bead_with_no_recorded_commit_is_not_audited(self):
        git = FakeGit(main=["a1"], origin_main=["a1"])
        code, _ = self.run_tool(
            ["--audit"],
            git,
            beads=[self.bead("SpatialEngine-fhf", "closed",
                             "no commit was ever recorded here")])
        self.assertEqual(code, 0)

    def test_a_rebased_commit_is_accounted_for_not_stranded(self):
        # The tick force-pushed a rebased branch: the recorded sha is gone but
        # the work is on origin/main. Reporting that as loss would train the
        # coordinator to ignore the audit.
        git = FakeGit(main=["a1", "n1"], origin_main=["a1", "n1"])
        git.know("0a1b2c3", equivalent_to="-")
        code, harness = self.run_tool(
            ["--audit"],
            git,
            beads=[self.bead("SpatialEngine-u2x.21.2", "closed",
                             "merged as 0a1b2c3 on bd/SpatialEngine-u2x.21.2")])
        self.assertEqual(code, 0)
        self.assertIn("rebased", " ".join(harness.lines))

    def test_a_sha_this_repo_does_not_have_is_unknown_not_stranded(self):
        git = FakeGit(main=["a1"], origin_main=["a1"])
        code, harness = self.run_tool(
            ["--audit"],
            git,
            beads=[self.bead("SpatialEngine-t9", "closed",
                             "merged as 0123456789abcdef on bd/SpatialEngine-t9")])
        self.assertEqual(code, 0)
        self.assertIn("unknown", " ".join(harness.lines))


class MergeGateTests(unittest.TestCase):
    """The verify half of the close gate is the FAST lane (ADR-0134).

    ADR-0118 tiered the lanes and made `--full` the merge gate;
    SpatialEngine-u2x.51 then had to name it explicitly, because a bare
    `eng/verify.sh` under that record is the scoped build gate and merging on
    it is the loss `SpatialEngine-xbz` was written to stop. ADR-0134 amends
    that clause: the merge gate is the fast lane, because at hundreds of merges
    a day the 15–25 minute lane is the price of every merge rather than signal.

    What must survive the amendment is everything that costs seconds and is the
    actual gate — the fetch, the unpublished-`main` refusal, the red-gate abort,
    the publish, and the ancestor check before `bd close`.
    """

    def setUp(self):
        self.module = load_script()

    def run_tool(self, argv, git, **kwargs):
        harness = Harness(git, self.module, **kwargs)
        code = self.module.main(argv, run=harness, out=harness)
        return code, harness

    def a_merged_bead(self, **kwargs):
        git = FakeGit(main=["a1"], origin_main=["a1"])
        git.branch("bd/SpatialEngine-u2x.51", ["a1", "w1"])
        return self.run_tool(["--bead", "SpatialEngine-u2x.51"], git, **kwargs)

    def test_the_merge_gate_is_the_fast_lane(self):
        code, harness = self.a_merged_bead()
        self.assertEqual(code, 0)
        calls = harness.verify_calls()
        self.assertEqual(len(calls), 1, calls)
        self.assertEqual(calls[0][1:], ["--fast"])

    def test_the_lane_is_always_named_and_never_the_full_one_by_accident(self):
        # A bare invocation is the build gate under ADR-0118 and `--full` is 20
        # minutes; the merge names the lane it means, so neither CI=true nor a
        # future edit to the default can decide what a merge costs.
        code, harness = self.a_merged_bead()
        self.assertEqual(code, 0)
        for call in harness.verify_calls():
            self.assertEqual(call[1:], ["--fast"])

    def test_the_exhaustive_lane_is_an_opt_in(self):
        git = FakeGit(main=["a1"], origin_main=["a1"])
        git.branch("bd/SpatialEngine-u2x.51", ["a1", "w1"])
        code, harness = self.run_tool(
            ["--bead", "SpatialEngine-u2x.51", "--full"], git)
        self.assertEqual(code, 0)
        self.assertEqual(harness.verify_calls()[0][1:], ["--full"])
        close = harness.called("bd", "close")[0]
        self.assertIn("--full", " ".join(close))

    def test_the_fast_lane_runs_on_the_rebased_branch_before_the_merge(self):
        # The merge gate is a run on the rebased branch, not on the branch as
        # it was cut: origin/main moved, and that is what has to be green.
        git = FakeGit(main=["a1"], origin_main=["a1", "o9"])
        git.branch("bd/SpatialEngine-u2x.51", ["a1", "w1"])
        git.worktree("bd/SpatialEngine-u2x.51", "/wt/x51")
        code, harness = self.run_tool(["--bead", "SpatialEngine-u2x.51"], git)
        self.assertEqual(code, 0)
        order = [c[:2] for c in harness.git.calls]
        verify_at = next(n for n, c in enumerate(order)
                         if c[0].endswith("verify.sh"))
        self.assertLess(order.index(["git", "rebase"]), verify_at)
        self.assertLess(verify_at, order.index(["git", "merge"]))

    def test_a_red_gate_aborts_the_merge_and_the_close(self):
        git = FakeGit(main=["a1"], origin_main=["a1"])
        git.branch("bd/SpatialEngine-u2x.51", ["a1", "w1"])
        code, harness = self.run_tool(
            ["--bead", "SpatialEngine-u2x.51"], git, verify_ok=False)
        self.assertEqual(code, 1)
        self.assertEqual(harness.closed(), [])
        self.assertEqual(harness.called("git", "merge"), [])
        self.assertEqual(harness.verify_calls()[0][1:], ["--fast"])
        self.assertIn("--fast", " ".join(harness.lines))

    def test_a_named_skip_reaches_the_lane_and_the_close_reason(self):
        # A merge that leaned on CI for a suite has to say so twice: in the
        # command that ran, and in the record `bd close` writes.
        git = FakeGit(main=["a1"], origin_main=["a1"])
        git.branch("bd/SpatialEngine-6g7", ["a1", "w1"])
        code, harness = self.run_tool(
            ["--bead", "SpatialEngine-6g7", "--skip-tests", "Host.Tests"],
            git)
        self.assertEqual(code, 0)
        self.assertEqual(harness.verify_calls()[0][1:],
                         ["--fast", "--skip-tests=Host.Tests"])
        close = " ".join(harness.called("bd", "close")[0])
        self.assertIn("Host.Tests", close)
        self.assertIn("CI", close)

    def test_cancelling_the_gate_leaves_the_bead_open(self):
        # A long lane gets interrupted. The tool must not treat that as a gate,
        # and must not merge or close on the way out.
        code, harness = self.a_merged_bead(verify_cancels=True)
        self.assertNotEqual(code, 0)
        self.assertEqual(harness.closed(), [])
        self.assertEqual(harness.called("git", "merge"), [])

    def test_a_declared_gate_run_still_needs_the_publish_gate(self):
        # --verified is an escape hatch for re-running after a failed push, not
        # a way to close without publishing.
        git = FakeGit(main=["a1"], origin_main=["a1"])
        git.branch("bd/SpatialEngine-u2x.51", ["a1", "w1"])
        code, harness = self.run_tool(
            ["--bead", "SpatialEngine-u2x.51", "--verified"], git)
        self.assertEqual(code, 0)
        self.assertEqual(harness.verify_calls(), [])
        self.assertEqual(harness.closed(), ["SpatialEngine-u2x.51"])
        close = harness.called("bd", "close")[0]
        self.assertIn("--fast", " ".join(close))
        self.assertIn("declared green", " ".join(close))

    def test_the_old_full_verified_spelling_still_works(self):
        # Runbooks and muscle memory both say --full-verified; renaming the
        # flag out from under them would fail a re-run mid-incident.
        git = FakeGit(main=["a1"], origin_main=["a1"])
        git.branch("bd/SpatialEngine-u2x.51", ["a1", "w1"])
        code, harness = self.run_tool(
            ["--bead", "SpatialEngine-u2x.51", "--full-verified"], git)
        self.assertEqual(code, 0)
        self.assertEqual(harness.verify_calls(), [])
        self.assertEqual(harness.closed(), ["SpatialEngine-u2x.51"])

    def test_a_declared_gate_run_still_fails_on_an_unpushed_merge(self):
        git = FakeGit(main=["a1"], origin_main=["a1"], push_fails=True)
        git.branch("bd/SpatialEngine-u2x.51", ["a1", "w1"])
        code, harness = self.run_tool(
            ["--bead", "SpatialEngine-u2x.51", "--verified"], git)
        self.assertEqual(code, 1)
        self.assertEqual(harness.closed(), [])


class DocumentedGateTests(unittest.TestCase):
    """The prose that tells a coordinator what to run must name the tool."""

    def setUp(self):
        self.repo = SCRIPT.parent.parent

    def read(self, relative):
        return (self.repo / relative).read_text(encoding="utf-8")

    def test_agents_md_close_rule_points_at_the_gate(self):
        text = self.read("AGENTS.md")
        self.assertIn("bd-merge-bead.py", text)

    def test_runbook_merge_step_uses_the_tool(self):
        text = self.read("eng/swarm-runbook.md")
        self.assertIn("bd-merge-bead.py", text)

    def test_agents_md_merge_rule_names_the_merge_gate(self):
        # ADR-0134 moved the merge gate off --full; a document that still says
        # the coordinator runs --full before a merge is sending the next
        # coordinator back to 20 minutes.
        text = self.read("AGENTS.md")
        merge_bullet = self.bullet_about(text, "- Merge and complete:")
        self.assertNotIn("runs `eng/verify.sh --full`", merge_bullet)
        self.assertIn("bd-merge-bead.py --bead", merge_bullet)

    def test_the_agents_md_merge_anchor_is_the_labels_own_bullet(self):
        # Anchoring on the tool's name alone lands on whichever bullet mentions
        # it *first* — today the `--full` lane bullet's `--bead <id> --full`
        # aside, which passed every assertion in the test above for the wrong
        # reason. Anchor on the bullet's own label, and assert the locator is
        # unambiguous rather than first-mention-wins.
        sample = ("- `eng/verify.sh --full` — opt in per merge\n"
                  "  (`bd-merge-bead.py --bead <id> --full`).\n"
                  "- Merge and complete: `bd-merge-bead.py --bead <id>`.\n")
        bullet = self.bullet_about(sample, "- Merge and complete:")
        self.assertNotIn("--full", bullet)
        self.assertIn("bd-merge-bead.py --bead <id>`.", bullet)

    def test_runbook_merge_step_names_the_tool_not_a_manual_lane(self):
        text = self.read("eng/swarm-runbook.md")
        merge_step = self.bullet_about(text, "bd-merge-bead.py --bead")
        self.assertIn("bd-merge-bead.py --bead", merge_step)

    def test_runbook_keeps_the_lane_contract_the_merge_tool_runs(self):
        # The two bullets extend each other now rather than replacing: the
        # worker section still states what each lane is and who runs it.
        text = self.read("eng/swarm-runbook.md")
        lanes = self.numbered_step_about(text, "Lanes.")
        self.assertIn("--full", lanes)
        self.assertIn("--fast", lanes)

    def test_the_lane_step_anchor_survives_renumbering(self):
        # The runbook's worker steps get inserted, not appended; a hard-coded
        # ordinal turns the next inserted step into a red build (CI 36707727501).
        # The locator is the step's title, whatever number precedes it.
        sample = ("1. Do a thing.\n"
                  "2. Lanes. --fast is the agent's lane and --full is CI's.\n"
                  "   More about --fast.\n"
                  "3. Hand off.\n")
        lanes = self.numbered_step_about(sample, "Lanes.")
        self.assertIn("--fast", lanes)
        self.assertIn("--full", lanes)
        self.assertIn("More about", lanes)
        self.assertNotIn("Hand off", lanes)

    def test_agents_md_hands_off_on_the_fast_lane(self):
        text = self.read("AGENTS.md")
        hand_off = self.bullet_about(text, "- Hand off:")
        self.assertIn("eng/verify.sh", hand_off)
        # An agent's step is the fast gate. It may say `--full` is somebody
        # else's, but it never runs it before a hand-off, and formatting is not
        # a per-hand-off cost either (ADR-0134).
        self.assertNotIn("eng/verify.sh --full", hand_off)
        self.assertNotIn("eng/verify.sh --format", hand_off)

    def numbered_step_about(self, text, title):
        """The numbered list item titled `title`, whatever number it wears.

        Ordinals churn: a step inserted in the middle of a runbook renumbers
        everything below it, so an anchor of "5. Lanes." is a renumbering
        tripwire rather than a contract gate. Match the title instead.
        """
        pattern = re.compile(r"^\s*\d+\.\s+" + re.escape(title))
        return self.bullet_about(text, pattern)

    def bullet_about(self, text, marker):
        """The line naming `marker` plus every continuation line below it."""
        if hasattr(marker, "search"):
            lines = text.splitlines()
            start = next(n for n, line in enumerate(lines) if marker.search(line))
        else:
            lines = text.splitlines()
            start = next(n for n, line in enumerate(lines) if marker in line)
        out = [lines[start]]
        for line in lines[start + 1:]:
            if not line.startswith((" ", "\t")) or not line.strip():
                break
            out.append(line)
        return "\n".join(out)


if __name__ == "__main__":
    unittest.main()
