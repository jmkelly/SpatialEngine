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

    def __init__(self, git, module, verify_ok=True, beads=None):
        self.git = git
        self.module = module
        self.verify_ok = verify_ok
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


if __name__ == "__main__":
    unittest.main()
