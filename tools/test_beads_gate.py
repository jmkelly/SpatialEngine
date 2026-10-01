#!/usr/bin/env python3
"""The bead protocol and the hard walls are gates, not things to remember.

Run: python3 -m unittest tools/test_beads_gate.py

`SpatialEngine-imz.4`: the walls are already enforced in code
(`Core_has_no_dependencies`), but the protocol around them is prose in
`AGENTS.md` and `eng/swarm-runbook.md`, and it has already failed in-tree four
separate ways — three workers read a body naming bead `.2` while working `.1`,
`.3` and `.4`; one coordinator tick released eight leases whose agents were all
still running; two merges landed with conflict resolutions written by somebody
who did not hold the workers' context; and three commits exist purely to rescue
work the reclaim race orphaned. Prose that has failed is advice.

So the four mechanical checks the bead names are `tools/beads-gate.py`, and
every lane of `eng/verify.sh` runs it:

  1. a closed bead's merge records the bead it merged: a `Task: <id>` trailer
     naming it, on the merge commit or on the work it brought in;
  2. a commit touching `src/Spatial.Contracts/**` or `src/Spatial.Core/**`
     changes an ADR, or cites `ADR-NNNN` in its body — the AGENTS.md rule that
     "behaviour changes land contract, SDK, test and ADR updates together";
  3. every `ADR-NNNN` cited anywhere resolves to a record that exists;
  4. the ADR register is complete.

Checks 3 and 4 are G1/G2's, implemented once in `tools/arch-index.py` and
shared rather than reimplemented — `ADRSharedTests` below pins that the beads
gate reads arch-index's own entry points and reaches the same findings, so
there is one answer to "is that ADR citable" and not two.

Check 1 is judged from the merge commit that added the gate onwards
(`GATE_FROM`), the way ADR-0150's shape rule is judged from its own number: a
corpus written under no rule is grandfathered rather than rewritten, which is
what makes the rule adoptable. History written before the trailer existed
cannot be given the trailer, and a gate that fails on all of it is a gate
nobody runs.
"""
import json
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPT = REPO_ROOT / "tools" / "beads_gate.py"
VERIFY_SH = REPO_ROOT / "eng" / "verify.sh"
CI_YML = REPO_ROOT / ".github" / "workflows" / "ci.yml"

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from tools.beads_gate import (  # noqa: E402
    closed_bead_findings,
    load_beads,
    load_paseo,
    merge_bead,
    parked_lease_holds,
    reclaimed_lease_findings,
    shared_adr_findings,
    wall_findings,
)


def git(root: Path, *args: str) -> str:
    """One git call in a fixture repository."""
    done = subprocess.run(["git", *args], cwd=root, capture_output=True,
                          text=True, check=True)
    return done.stdout.strip()


def make_repo(root: Path) -> Path:
    """A repository with one commit, shaped like this one's history."""
    git(root, "init", "-q", "-b", "main")
    git(root, "config", "user.email", "gate@example.com")
    git(root, "config", "user.name", "Gate")
    write(root, "README.md", "# fixture\n")
    commit(root, "initial commit")
    return root


def write(root: Path, name: str, text: str) -> Path:
    path = root / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    git(root, "add", name)
    return path


def commit(root: Path, message: str) -> str:
    git(root, "commit", "-q", "--allow-empty", "-m", message)
    return git(root, "rev-parse", "HEAD")


def bead(bead_id: str, status: str = "closed") -> dict:
    return {"id": bead_id, "title": "work", "status": status,
            "closed_at": "2026-09-30T11:01:28Z"}


class WallTests(unittest.TestCase):
    """A change to a contract or to Core lands with a decision record.

    This is the acceptance case: `eng/verify.sh` must fail on a merge commit
    touching `Spatial.Contracts` or `Spatial.Core` with no ADR change in the
    diff and no `ADR-NNNN` in the commit body.
    """

    def setUp(self):
        self.raw = tempfile.TemporaryDirectory()
        self.root = make_repo(Path(self.raw.name))
        self.base = git(self.root, "rev-parse", "HEAD")
        self.addCleanup(self.raw.cleanup)

    def findings(self):
        return wall_findings(self.root, base=self.base)

    def test_a_contract_change_with_no_record_is_a_finding(self):
        # The reproduction. `AGENTS.md` says behaviour changes land contract,
        # SDK, test and ADR updates together; nothing read that sentence.
        commit(self.root, "Add a query parameter to the feature service")
        write(self.root, "src/Spatial.Contracts/IFeatureService.cs", "// v2\n")
        commit(self.root, "Feature service gains a second parameter")
        found = self.findings()
        self.assertTrue(
            any("Spatial.Contracts" in finding for finding in found),
            f"a contract change with no ADR was not a finding: {found}")

    def test_a_core_change_with_no_record_is_a_finding(self):
        write(self.root, "src/Spatial.Core/Envelope.cs", "// v2\n")
        commit(self.root, "Core envelope gains a field")
        found = self.findings()
        self.assertTrue(any("Spatial.Core" in finding for finding in found),
                        f"a Core change with no ADR was not a finding: {found}")

    def test_an_adr_changed_in_the_same_commit_satisfies_the_wall(self):
        write(self.root, "src/Spatial.Contracts/IFeatureService.cs", "// v2\n")
        write(self.root, "architecture/decisions/ADR-0152-a-decision.md", "# ADR\n")
        commit(self.root, "Feature service gains a second parameter")
        self.assertEqual(self.findings(), [])

    def test_an_adr_cited_in_the_commit_body_satisfies_the_wall(self):
        # The other half of the rule as the bead states it: a record the
        # change amends without touching is cited, not rewritten.
        write(self.root, "src/Spatial.Core/Envelope.cs", "// v2\n")
        git(self.root, "commit", "-q", "--allow-empty", "-m",
            "Core envelope gains a field\n\nEnvelope identity per ADR-0087.")
        self.assertEqual(self.findings(), [])

    def test_a_merge_commit_that_brings_the_change_is_judged(self):
        # The acceptance case names a *merge* commit, and the merge itself is
        # where the wall is judged: `git log base..HEAD` walks the merged-in
        # commits too, so a wall change cannot be laundered by merging it.
        git(self.root, "checkout", "-q", "-b", "bd/SpatialEngine-aaa.1")
        write(self.root, "src/Spatial.Contracts/IFeatureService.cs", "// v2\n")
        commit(self.root, "A contract change with no record\n\nTask: SpatialEngine-aaa.1")
        git(self.root, "checkout", "-q", "main")
        git(self.root, "merge", "-q", "--no-ff", "-m",
            "Merge SpatialEngine-aaa.1: a contract change", "bd/SpatialEngine-aaa.1")
        found = self.findings()
        self.assertTrue(any("Spatial.Contracts" in finding for finding in found),
                        f"a merged contract change with no ADR was not a finding: {found}")

    def test_a_change_outside_the_walls_is_not_judged(self):
        # The walls are two directories, not "every code change": a host change
        # with no ADR is ordinary work and a gate that failed it would be a
        # gate nobody ran.
        write(self.root, "src/Spatial.Host/Host.cs", "// v2\n")
        write(self.root, "tools/some-tool.py", "# v2\n")
        commit(self.root, "Host wiring and a tool")
        self.assertEqual(self.findings(), [])

    def test_a_sibling_project_with_a_wall_prefix_is_not_a_wall(self):
        # The rule is the two directories, not every name that begins with one:
        # `Spatial.Contracts.Tests` holds no contract, so a test suite added
        # there is ordinary work and a gate that failed it would be a gate
        # nobody ran.
        write(self.root, "src/Spatial.Contracts.Tests/ContractShapeTests.cs",
              "// new suite\n")
        commit(self.root, "A suite that pins the contract shape")
        self.assertEqual(self.findings(), [])


class TrailerTests(unittest.TestCase):
    """A closed bead's merge records the bead it merged, and names that bead.

    The mis-dispatch the substitution rule was written for — three workers each
    reading a body naming bead `.2` while working `.1`, `.3` and `.4` — is
    exactly a set of commits whose bead and whose `Task:` id disagree, so both
    halves are checked: the trailer is missing, and the trailer names another
    bead. The trailer is read on the merge commit *and* on the work it brought
    in, because the protocol's trailer is written by whoever does the work and
    a merge written by `git merge --no-ff -m` carries a subject and nothing
    else — which is what failed every lane from 7014ef4 on (SpatialEngine-5ak).
    """

    def setUp(self):
        self.raw = tempfile.TemporaryDirectory()
        self.root = make_repo(Path(self.raw.name))
        self.addCleanup(self.raw.cleanup)
        self.gate_from = git(self.root, "rev-parse", "HEAD")

    def branch(self, bead_id: str, message: str | None = None) -> str:
        """One bead's branch, with a work commit that carries its trailer."""
        git(self.root, "checkout", "-q", "-b", f"bd/{bead_id}")
        write(self.root, f"src/{bead_id}.cs", "// work\n")
        commit(self.root, message or f"work for {bead_id}\n\nTask: {bead_id}")
        return f"bd/{bead_id}"

    def merge_branch(self, branch: str, message: str) -> str:
        git(self.root, "checkout", "-q", "main")
        git(self.root, "merge", "-q", "--no-ff", "-m", message, branch)
        return git(self.root, "rev-parse", "HEAD")

    def merge_bead(self, bead_id: str, message: str) -> str:
        return self.merge_branch(self.branch(bead_id), message)

    def findings(self, beads):
        return closed_bead_findings(self.root, beads=beads, gate_from=self.gate_from)

    def test_a_merge_commit_with_the_matching_trailer_is_not_a_finding(self):
        self.merge_bead("SpatialEngine-aaa.1",
                        "Merge SpatialEngine-aaa.1: work\n\nTask: SpatialEngine-aaa.1")
        self.assertEqual(self.findings([bead("SpatialEngine-aaa.1")]), [])

    def test_a_merge_subject_with_no_description_after_the_id_is_read(self):
        # `tools/bd-merge-bead.py` writes `Merge <id>: <subject>`, and the id
        # followed by a colon is the only shape the parser had — so a hand
        # merged `Merge <id>` read as a merge naming no bead and was skipped
        # rather than judged.
        self.merge_bead("SpatialEngine-aaa.1", "Merge SpatialEngine-aaa.1")
        self.assertEqual(merge_bead("Merge SpatialEngine-aaa.1"), "SpatialEngine-aaa.1")
        self.assertEqual(
            self.findings([bead("SpatialEngine-aaa.1")]), [])

    def test_a_plain_git_merge_is_not_a_bead_merge(self):
        # `Merge branch 'x' of ...` is not this protocol's commit, and the
        # looser subject read must not start calling it one.
        for subject in ("Merge branch 'bd/x' of /tmp/other",
                        "Merge pull request #12 from example/branch",
                        "Revert \"Merge SpatialEngine-aaa.1: work\""):
            self.assertIsNone(merge_bead(subject), subject)

    def test_a_merge_commit_with_no_trailer_is_judged_over_the_work_commits(self):
        # SpatialEngine-5ak: the reproduction. Every merge on `main` writes a
        # subject and nothing else — `git merge --no-ff -m "Merge <id>: work"`
        # — so judging the merge commit's own body failed every lane from
        # 7014ef4 onwards, over a merge whose work commit carried the trailer.
        # The trailer names the bead a merge records, and `git log
        # --grep='Task: <id>'` reads it off the work; judging the merge body
        # alone was judging a commit the protocol never asked for the trailer.
        self.merge_bead("SpatialEngine-aaa.1",
                        "Merge SpatialEngine-aaa.1: the work, with a description")
        self.assertEqual(self.findings([bead("SpatialEngine-aaa.1")]), [])

    def test_a_merge_of_a_bead_the_queue_has_not_closed_yet_is_not_a_missed_bead(self):
        # The other half of 7014ef4. `bd show SpatialEngine-imz.4` exists and
        # is IN_PROGRESS — a merge is published and the close is a separate,
        # later step — so a gate that read only the closed list called a real
        # bead one the queue has "no record of", and failed every lane in the
        # repository for a merge that had not been closed yet.
        self.merge_bead("SpatialEngine-imz.4",
                        "Merge SpatialEngine-imz.4: keep the bead gate off the queue")
        self.assertEqual(
            self.findings([bead("SpatialEngine-imz.4", status="in_progress")]), [])

    def test_a_merge_with_no_trailer_anywhere_is_a_finding(self):
        # The check that survives the move to the work commits: a branch whose
        # commits name no bead at all is work `git log --grep='Task: <id>'`
        # cannot find, which is what the rule was written to catch.
        self.merge_branch(self.branch("SpatialEngine-aaa.1", "work for the bead"),
                          "Merge SpatialEngine-aaa.1: work")
        found = self.findings([bead("SpatialEngine-aaa.1")])
        self.assertTrue(
            any("Task:" in finding for finding in found),
            f"a merge with no Task trailer on it or on its work was not a finding: {found}")

    def test_work_whose_trailer_names_another_bead_is_a_finding(self):
        # The mis-dispatch, read where it actually happens: bead `.4`'s work
        # carried bead `.2`'s id, and the merge commit named `.4` because
        # `bd-merge-bead.py` composes the trailer from the id it was handed.
        self.merge_branch(self.branch("SpatialEngine-aaa.4", "work\n\nTask: SpatialEngine-aaa.2"),
                          "Merge SpatialEngine-aaa.4: work\n\nTask: SpatialEngine-aaa.4")
        found = self.findings([bead("SpatialEngine-aaa.4")])
        self.assertTrue(
            any("SpatialEngine-aaa.2" in finding and "SpatialEngine-aaa.4" in finding
                for finding in found),
            f"work carrying another bead's trailer was not a finding: {found}")

    def test_a_merge_commit_for_an_unknown_bead_is_reported_once(self):
        # A merge the queue has no record of is one finding, not two: the
        # unknown-bead report fell through to the trailer check and printed
        # both halves of a complaint about the same commit.
        self.merge_bead("SpatialEngine-aaa.1",
                        "Merge SpatialEngine-aaa.1: work\n\nTask: SpatialEngine-aaa.1")
        self.assertEqual(len(self.findings([])), 1)

    def test_a_trailer_naming_another_bead_is_a_finding(self):
        # The mis-dispatch, read mechanically: bead `.4`'s work carried bead
        # `.2`'s id, and nothing noticed until three agents had each built the
        # wrong thing.
        self.merge_bead("SpatialEngine-aaa.4",
                        "Merge SpatialEngine-aaa.4: work\n\nTask: SpatialEngine-aaa.2")
        found = self.findings([bead("SpatialEngine-aaa.4")])
        self.assertTrue(
            any("SpatialEngine-aaa.2" in finding and "SpatialEngine-aaa.4" in finding
                for finding in found),
            f"a mismatched Task trailer was not a finding: {found}")

    def test_a_bead_that_is_not_closed_is_not_judged(self):
        self.merge_bead("SpatialEngine-aaa.1", "Merge SpatialEngine-aaa.1: work")
        self.assertEqual(self.findings([bead("SpatialEngine-aaa.1", status="open")]), [])

    def test_merges_older_than_the_gate_are_grandfathered_and_counted(self):
        # A corpus written under no rule is grandfathered rather than
        # rewritten (the shape rule's precedent, ADR-0150): the trailer did not
        # exist before this gate, so a merge from before it is reported as
        # skipped, not failed — and the count is printed, because a scope that
        # is invisible is indistinguishable from a check that passed.
        self.merge_bead("SpatialEngine-aaa.1", "Merge SpatialEngine-aaa.1: work")
        self.gate_from = commit(self.root, "the gate lands")
        beads = [bead("SpatialEngine-aaa.1")]
        self.assertEqual(self.findings(beads), [])
        report = report_for(self.root, beads=beads, gate_from=self.gate_from)
        self.assertIn("grandfathered", report)

    def test_a_merge_commit_for_an_unknown_bead_is_a_finding(self):
        # A merge whose subject names a bead the queue does not have is a
        # merge whose close will never be justified, and is worth reading.
        self.merge_bead("SpatialEngine-aaa.1",
                        "Merge SpatialEngine-aaa.1: work\n\nTask: SpatialEngine-aaa.1")
        found = self.findings([])
        self.assertTrue(any("SpatialEngine-aaa.1" in finding for finding in found),
                        f"a merge of an unknown bead was not a finding: {found}")


class ReclaimedLeaseTests(unittest.TestCase):
    """A lease that went while the agent still held it is a finding.

    The lease race `SpatialEngine-u2x.30` recorded: `bd reclaim` keys on lease
    age alone, one coordinator tick released eight leases whose agents `paseo`
    still reported as running, and three commits exist only to rescue the work
    that orphaned (`WIP: preserve uncommitted work ... swarm reclaimed stale
    lease`). `bd` exposes no registration surface for a reclaim hook
    (`bd config set hooks.pre-commit` is refused by name; the only chaining
    `bd hooks install` offers is the content outside the managed markers in a
    *git* hook, which no reclaim runs — SpatialEngine-8oe), so the policy has
    to be enforced from the other side: a bead whose lease is gone while a live
    agent is still sitting in its worktree is the shape of the race, read
    mechanically.
    """

    AGENT_ID = "8d15cb2d-76a1-4f5f-b388-e8776e72d86d"

    def agent(self, status="running", cwd="~/worktrees/bd-spatialengine-aaa-1"):
        return {"id": self.AGENT_ID, "shortId": self.AGENT_ID[:7],
                "status": status, "cwd": cwd}

    def unleased(self, bead_id="SpatialEngine-aaa.1", notes=None):
        """A bead whose lease a reclaim took back: open, unassigned, no lease."""
        return {"id": bead_id, "status": "open", "assignee": "",
                "lease_expires_at": None,
                "notes": notes if notes is not None else
                f"687eb2e cdbfd61 bd/{bead_id} agent {self.AGENT_ID} "
                "workspace wks_327ef32935e91319"}

    def findings(self, beads, agents=None, workspaces=()):
        return reclaimed_lease_findings(
            beads, agents=[self.agent()] if agents is None else agents,
            workspaces=list(workspaces))

    def test_a_bead_with_no_lease_and_a_live_agent_in_its_worktree_is_a_finding(self):
        # The reproduction: the lease is gone, the agent is running in the
        # bead's own worktree, and nothing read that pair back.
        found = self.findings([self.unleased()])
        self.assertTrue(
            any("SpatialEngine-aaa.1" in finding and self.AGENT_ID[:7] in finding
                for finding in found),
            f"a lease reclaimed out from under a live agent was not a finding: {found}")

    def test_the_finding_names_the_recovery_and_forbids_bare_reclaim(self):
        found = self.findings([self.unleased()])
        self.assertTrue(any("bd-safe-reclaim.py" in finding for finding in found),
                        f"the finding does not say what to do: {found}")
        self.assertTrue(any("bd reclaim" in finding for finding in found),
                        f"the finding does not forbid the primitive that caused it: {found}")

    def test_the_worktree_match_is_enough_when_the_notes_name_no_agent(self):
        # The other half of the hold, and the one that survives a notes field
        # nobody filled in: paseo still lists the workspace and the agent in it.
        bead = self.unleased(notes="687eb2e bd/SpatialEngine-aaa.1")
        workspaces = [{"name": "bd/SpatialEngine-aaa.1",
                       "cwd": "/home/james/.paseo/worktrees/1mmcart7/"
                              "bd-spatialengine-aaa-1"}]
        found = self.findings([bead], workspaces=workspaces)
        self.assertTrue(any("SpatialEngine-aaa.1" in finding for finding in found),
                        f"a live agent in the worktree was not read as holding it: {found}")

    def test_a_claimed_bead_holding_its_lease_is_not_a_finding(self):
        # The ordinary case, and the one a gate that failed it would fail every
        # lane of the swarm: in progress, leased, agent running.
        beads = [dict(self.unleased(), status="in_progress",
                      assignee="James Kelly",
                      lease_expires_at="2026-10-01T03:33:55Z")]
        self.assertEqual(self.findings(beads), [])

    def test_a_closed_bead_is_not_a_finding(self):
        self.assertEqual(
            self.findings([dict(self.unleased(), status="closed")]), [])

    def test_an_unleased_bead_with_no_agent_on_it_is_not_a_finding(self):
        # What `bd reclaim` is *for*: a crashed worker's bead, reclaimed with
        # nobody left holding it.
        self.assertEqual(self.findings([self.unleased()], agents=[]), [])

    def test_an_agent_that_is_not_running_does_not_hold_a_lease(self):
        # `paseo` reports `closed` for a finished session, and a bead whose
        # worker ended cleanly is not the race.
        for status in ("closed", "stopped", "unknown"):
            self.assertEqual(self.findings([self.unleased()],
                                           agents=[self.agent(status=status)]), [],
                             f"a {status} agent was read as holding a lease")

    def test_an_idle_session_is_reported_rather_than_failed(self):
        # The boundary, and the reason for it: an idle session parked on a
        # bead whose work is already finished or superseded is a coordinator's
        # business, not a lane's. Two such beads existed in this repository on
        # the day the check landed, and a gate that failed them would have been
        # a gate nobody could merge from.
        parked = parked_lease_holds(
            [self.unleased()], [self.agent(status="idle")], [])
        self.assertEqual(parked, ["SpatialEngine-aaa.1"])
        self.assertEqual(self.findings([self.unleased()],
                                       agents=[self.agent(status="idle")]), [])

    def test_a_running_agent_is_not_counted_as_parked(self):
        self.assertEqual(
            parked_lease_holds([self.unleased()], [self.agent()], []), [])

    def test_an_agent_in_another_worktree_holds_nothing(self):
        # Neither half of the hold matches: not the worktree, and — the part
        # that catches a lazy test — not the agent id the notes recorded, so
        # the second lookup cannot rescue the first one's miss.
        agents = [self.agent(cwd="~/worktrees/bd-spatialengine-aaa-11")]
        bead = self.unleased(notes="687eb2e bd/SpatialEngine-aaa.1")
        self.assertEqual(self.findings([bead], agents=agents), [])

    def test_the_hold_is_decided_by_the_reclaim_wrapper_not_a_second_answer(self):
        # One answer to "is this agent live", read through the tool that is the
        # documented fix, or the gate and the wrapper drift apart.
        from tools import beads_gate
        module = beads_gate.load_reclaim(REPO_ROOT)
        self.assertEqual(module.__name__, "bd_safe_reclaim")
        self.assertTrue(callable(module.protect_reason))

    def test_the_paseo_reads_are_reported_when_paseo_cannot_be_run(self):
        # The queue's own boundary (ADR-0152): an input the run cannot read is
        # reported as not judged, never as a pass.
        class Failed:
            returncode = 127
            stdout = ""
            stderr = "paseo: command not found"

        agents, workspaces, note = load_paseo(Path("/nowhere"), run=lambda cmd: Failed())
        self.assertIsNone(agents)
        self.assertIsNone(workspaces)
        self.assertIn("paseo", note)


class QueueReadTests(unittest.TestCase):
    """The gate reads every bead the queue has, not only the closed ones.

    `load_beads` shells out to `bd`, so this drives it against a stub `bd` on
    PATH rather than the live queue — `tools/test_no_real_queue.py` makes
    reaching the real database a property of the suite, and the queue is
    coordination state whose closed set is not the set of beads that exist.
    """

    def test_an_open_bead_is_read_as_a_bead_the_queue_has(self):
        beads, note = load_beads(self.root)
        self.assertEqual(note, "")
        self.assertEqual(sorted(bead["id"] for bead in beads),
                         ["SpatialEngine-aaa.1", "SpatialEngine-aaa.2"])
        self.assertIn("--all", self.args.read_text(encoding="utf-8"))

    def setUp(self):
        self.raw = tempfile.TemporaryDirectory()
        self.addCleanup(self.raw.cleanup)
        self.root = Path(self.raw.name)
        self.args = self.root / "args"
        # A stub that honours `--status` and `--all` the way `bd list` does, so
        # a caller that asks for the closed set alone is not handed the rest.
        stub = self.root / "bd"
        stub.write_text(textwrap.dedent(f"""\
            #!/usr/bin/env python3
            import json, sys
            args = sys.argv[1:]
            open("{self.args}", "a").write(" ".join(args) + "\\n")
            beads = [{{"id": "SpatialEngine-aaa.1", "status": "closed"}},
                     {{"id": "SpatialEngine-aaa.2", "status": "in_progress"}}]
            if "--all" not in args:
                wanted = args[args.index("--status") + 1] if "--status" in args else None
                beads = [b for b in beads if b["status"] == wanted]
            print(json.dumps(beads))
            """), encoding="utf-8")
        stub.chmod(0o755)
        previous = os.environ.get("PATH", "")
        os.environ["PATH"] = f"{self.root}{os.pathsep}{previous}"
        self.addCleanup(os.environ.__setitem__, "PATH", previous)


def report_for(root: Path, beads, gate_from: str) -> str:
    """The gate's own report, so a scope that is skipped says so in words."""
    done = subprocess.run(
        [sys.executable, str(SCRIPT), "--root", str(root),
         "--base", gate_from,
         "--watermark", gate_from,
         "--no-paseo",
         "--beads", write_beads(root, beads)],
        capture_output=True, text=True)
    return (done.stdout or "") + (done.stderr or "")


def write_beads(root: Path, beads) -> str:
    path = root / "beads.json"
    path.write_text(json.dumps(beads), encoding="utf-8")
    return str(path)


class ShippedRepositoryTests(unittest.TestCase):
    """What the gate says about this repository, today."""

    def test_the_repository_is_green_and_says_what_it_did_not_judge(self):
        # A gate that is red on `main` at the moment it lands is a gate that
        # gets disabled. Run over the range the gate itself introduced, with
        # no queue: the wall check is judged and check 1 says out loud that it
        # had no bead list to read rather than passing silently. (`--no-queue`
        # rather than letting the test shell out to `bd`, and `--no-paseo`
        # rather than letting it shell out to `paseo`, which
        # `tools/test_no_real_queue.py` forbids for the whole tooling suite.)
        done = subprocess.run(
            [sys.executable, str(SCRIPT), "--root", str(REPO_ROOT),
             "--base", "HEAD", "--no-queue", "--no-paseo"],
            capture_output=True, text=True)
        output = (done.stdout or "") + (done.stderr or "")
        self.assertEqual(done.returncode, 0, output[-4000:])
        self.assertIn("not judged", output)

    def test_the_gate_writes_the_merge_trailer_bd_merge_bead_would_omit(self):
        # The close half: bd-merge-bead.py is what writes the merge commit, so
        # the trailer has to come from there or check 1 is unsatisfiable.
        import importlib.util
        spec = importlib.util.spec_from_file_location(
            "bd_merge_bead", REPO_ROOT / "tools" / "bd-merge-bead.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        self.assertEqual(module.merge_message("SpatialEngine-aaa.1", "a fix"),
                         "Merge SpatialEngine-aaa.1: a fix\n\nTask: SpatialEngine-aaa.1")


class ADRSharedTests(unittest.TestCase):
    """Checks 3 and 4 are arch-index's, read through arch-index."""

    def test_the_shared_reads_are_the_ones_arch_index_runs(self):
        from tools import beads_gate
        arch = beads_gate.load_arch_index(REPO_ROOT)
        root = REPO_ROOT
        corpus = arch.load_corpus(root)
        shared = shared_adr_findings(root)
        self.assertEqual(sorted(shared["citations"]), sorted(arch.dangling_citations(root, corpus)))
        self.assertEqual(sorted(shared["register"]), sorted(arch.register_findings(root, corpus)))
        self.assertEqual(sorted(shared["corpus"]), sorted(arch.corpus_findings(root, corpus)))


class LaneWiringTests(unittest.TestCase):
    """A rule only the tooling suite runs is not a gate.

    The shape ADR-0143 and ADR-0146 give the other repository checks, and the
    reason is the same: `tools/test_*.py` runs only when the change set touches
    `tools/**`, and both of these failures arrive on a `src` merge.
    """

    def lanes(self):
        text = VERIFY_SH.read_text(encoding="utf-8")
        markers = ["# --- the format lane",
                   "# --- the full lane",
                   "# --- the default lane"]
        self.assertTrue(all(m in text for m in markers),
                        "eng/verify.sh no longer carries the lane markers this test splits on")
        blocks = []
        for index, marker in enumerate(markers):
            end = (text.index(markers[index + 1]) if index + 1 < len(markers)
                   else len(text))
            blocks.append(text[text.index(marker):end])
        return blocks

    def test_every_lane_runs_the_beads_gate(self):
        for block in self.lanes():
            self.assertIn("beads_gate_step", block)

    def test_the_step_calls_the_gate_rather_than_a_test(self):
        text = VERIFY_SH.read_text(encoding="utf-8")
        start = text.index("beads_gate_step() {")
        body = text[start:text.index("\n}", start)]
        self.assertIn("python3 tools/beads_gate.py", body)

    def test_the_beads_gate_is_a_repository_check_not_a_tooling_test(self):
        # A `tools/test_*.py` would run only on a change set that touches
        # `tools/**`, which is not the change set either failure arrives on.
        self.assertFalse((REPO_ROOT / "tools" / "test_trailer.py").exists())

    def test_ci_runs_the_gate_on_main(self):
        ci = CI_YML.read_text(encoding="utf-8")
        self.assertIn("tools/beads_gate.py", ci)


if __name__ == "__main__":
    unittest.main()
