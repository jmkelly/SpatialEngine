#!/usr/bin/env python3
"""The bead protocol and the hard walls are gates, not prose to remember.

    python3 tools/beads_gate.py                  # judge, as every lane does
    python3 tools/beads_gate.py --plan           # print what would be judged
    python3 tools/beads_gate.py --base origin/main
    python3 tools/beads_gate.py --beads closed.json --watermark <sha>
    python3 tools/beads_gate.py --adr            # include the ADR shared reads

The geometry and contract walls in this repository are already enforced in
code, by tests in `Spatial.Architecture.Tests`. The protocol *around* them is
not, and it is prose in `AGENTS.md` plus a runbook — and it has failed in-tree
four separate ways, all of them recorded in git rather than hypothesised
(SpatialEngine-imz.4):

  * the mis-dispatch the runbook's substitution rule was written for: three
    workers each read a body naming bead `.2` while working `.1`, `.3` and `.4`;
  * one coordinator tick released eight leases whose agents were all still
    running, because `bd reclaim` keys on lease age alone;
  * two merges landed with conflict resolutions written by a coordinator that
    did not hold the workers' context;
  * three commits exist purely to rescue work the reclaim race orphaned.

Prose that has already failed is advice. So five *mechanical* checks live here,
with no judgement in any of them:

  1. **the trailer.** A closed bead's merge records the bead it merged: the
     `Task: <id>` trailer is on the merge commit or on the work it merged, and
     the id it names is the bead the commit is for. This is what a
     mis-dispatch looks like read mechanically — the bead and the trailer
     disagree — so both halves are checked, and a merge of a bead the queue
     does not have is reported too. The work commits count because that is
     where the protocol's trailer is written by an agent, and a merge commit
     written by `git merge --no-ff -m` carries only a subject: judging the
     merge body alone failed every lane in the repository from 7014ef4 on
     (SpatialEngine-5ak).

  2. **the wall.** A commit touching `src/Spatial.Contracts/**` or
     `src/Spatial.Core/**` changes an ADR, or cites `ADR-NNNN` in its body:
     `AGENTS.md`'s "behaviour changes land contract, SDK, test and ADR updates
     together", as a comparison rather than a review. Only those two
     directories are walls; a host change with no record is ordinary work.

  3. **the lease**, read against `paseo`: a bead with no lease while a live
   agent is still in its worktree is the shape of the reclaim race — `bd
   reclaim` keys on lease age alone, one tick released eight leases whose
   agents were all still running, and three commits exist purely to rescue the
   work that orphaned. `bd` exposes no registration surface for a reclaim hook
   (SpatialEngine-8oe, ADR-0162), so the policy is enforced from the other
   side: the damage is a finding rather than a hook, and the one question it
   asks — is this agent live — is asked through `tools/bd-safe-reclaim.py`, the
   documented fix, rather than answered a second time here.

4. **the citation**, and 5. **the register**: G1/G2's, implemented once in
     `tools/arch-index.py` and shared rather than reimplemented here, because
     two answers to "does that ADR resolve" is one too many and the one nobody
     runs is the one that ships. They are *reported* on every run and gated by
     `tools/arch-index.py --check`, which every lane already calls; `--adr`
     promotes them to findings for a standalone run.

Check 1 is judged from `GATE_FROM` — the merge commit that first brought this
file onto `main` — onwards, the way ADR-0150's shape rule is judged from the
number of the record that decided it. History written before the trailer
existed cannot be given the trailer, and a gate red on all of it is a gate
nobody runs. The grandfathered count is printed on every run, because a scope
that is invisible is indistinguishable from a check that passed.

Where the bead queue is the input, the queue is local coordination state and
not history: CI has no `.beads` database, so a run that cannot read the queue
says so in those words and judges the rest. `--strict` turns that into a
finding, for a caller that is on a machine where the queue must be there.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import re
import subprocess
import sys
from pathlib import Path

#: The two directories `AGENTS.md` calls hard walls, and the rule that guards
#: them: a behaviour change to either lands contract, SDK, test and ADR updates
#: together. Matched as directory prefixes, so a file directly inside the wall
#: counts and a project that merely starts with the same name (`src/
#: Spatial.Contracts.Tests`) does not — the rule is about the wall's own
#: surface, not about every directory whose name begins with it.
WALL_PATHS = ("src/Spatial.Contracts/", "src/Spatial.Core/")

#: Where a decision record lives. A commit that changes one of these has
#: satisfied the wall by editing the record rather than citing it.
DECISIONS_DIR = "architecture/decisions/"

#: `ADR-NNNN` in a commit body: the other way to satisfy the wall, for the
#: change that refines a record instead of rewriting it.
ADR_CITATION = re.compile(r"\bADR-(\d{4})\b")

#: A `Task:` trailer, as the commit protocol spells it: the bead the commit is
#: work for. Read from the whole body rather than the last paragraph, because
#: `git log --format=%b` puts the trailers last in a body `git commit -m`
#: wrote and a merge body is one paragraph.
TASK_TRAILER = re.compile(r"^Task:[ \t]+(?P<id>\S+)[ \t]*$", re.MULTILINE)

#: The merge subject `tools/bd-merge-bead.py` writes, which is what makes a
#: commit a bead's merge commit at all. `Merge <id>: <description>` is what the
#: tool writes and `Merge <id>` is what a hand merge of the same branch looks
#: like, so both are read: the id ends at the colon or at the end of the
#: subject, never at the space in `Merge branch 'x' of ...`, which is not this
#: protocol's commit.
MERGE_SUBJECT = re.compile(r"^Merge (?P<id>[A-Za-z0-9][\w.-]*)(?:: |$)")

#: Field separator and record separator for one `git log` walk: subjects and
#: bodies contain newlines and a NUL would appear in neither, but git will not
#: print a NUL in `--format`, so a unit separator and a record separator it
#: will. Unit is invisible in a hex dump of the output; a bug here is a wrong
#: answer rather than a crash, so the parsing is exercised by the unit tests.
UNIT = "\x1f"
RECORD = "\x1e"


class GitError(RuntimeError):
    """A git call the gate could not read, named rather than swallowed."""


def git(root: Path, *args: str) -> str:
    """One git call in `root`. Raises `GitError` with git's own words."""
    done = subprocess.run(["git", *args], cwd=root, capture_output=True,
                          text=True)
    if done.returncode != 0:
        detail = (done.stderr or done.stdout or "").strip().splitlines()
        raise GitError(f"git {' '.join(args)}: {detail[-1] if detail else 'failed'}")
    return done.stdout


class Commit:
    """One commit's subject, body and changed paths, as the gate reads them."""

    def __init__(self, sha: str, subject: str, body: str, paths=()):
        self.sha = sha
        self.subject = subject
        self.body = body
        self.paths = list(paths)

    @property
    def short(self) -> str:
        return self.sha[:7]

    @property
    def trailers(self) -> list[str]:
        """The bead ids this commit's `Task:` trailers name."""
        return TASK_TRAILER.findall(self.body)

    def cites_adr(self) -> bool:
        return bool(ADR_CITATION.search(self.body))

    def changes_a_record(self) -> bool:
        return any(path.startswith(DECISIONS_DIR) and path.endswith(".md")
                   for path in self.paths)

    def touches_a_wall(self) -> list[str]:
        """The wall paths this commit touches, in the order it touched them."""
        return [path for path in self.paths if path.startswith(WALL_PATHS)]


def walk(root: Path, revision: str = "HEAD", merges_only: bool = False) -> list[Commit]:
    """Every commit in `revision`, newest first, with its changed paths.

    One `git log` for the subjects and bodies and one `git show` per commit for
    the paths. A change set is tens of commits and the paths are what the wall
    is judged on, so there is no reading of them without asking git.
    """
    args = ["log", "--format=%H" + UNIT + "%s" + UNIT + "%b" + RECORD]
    if merges_only:
        args.append("--merges")
    args.append(revision)
    commits: list[Commit] = []
    for chunk in git(root, *args).split(RECORD):
        if not chunk.strip():
            continue
        parts = chunk.strip("\n").split(UNIT, 2)
        parts += [""] * (3 - len(parts))
        commits.append(Commit(parts[0].strip(), parts[1].strip(), parts[2].strip()))
    for commit in commits:
        commit.paths = git(root, "show", "--name-only", "--format=", commit.sha).split()
    return commits


def merge_bead(subject: str) -> str | None:
    """The bead a merge subject names, or None when it names none."""
    match = MERGE_SUBJECT.match(subject)
    return match.group("id") if match else None


def work_beads(root: Path, sha: str) -> list[str]:
    """The bead ids the `Task:` trailers name over the commits a merge brought in.

    The commits are the ones the merge's parents past the first have that the
    first parent does not already have — `M^1..M^2`, and the same for each
    further parent of an octopus — so the merge commit's own body is not read
    back here and each side is judged on its own. A range git will not read is
    reported as naming nothing rather than raising: the caller reads the merge
    commit's trailers separately, and a merge whose history cannot be walked
    has not thereby become a finding.
    """
    try:
        parents = git(root, "rev-list", "--parents", "-n", "1", sha).split()[1:]
    except GitError:
        return []
    named: list[str] = []
    for parent in parents[1:]:
        try:
            out = git(root, "log", "--format=%b" + RECORD, f"{sha}^1..{parent}")
        except GitError:
            continue
        named.extend(TASK_TRAILER.findall(out))
    return list(dict.fromkeys(named))


# --- check 1: the trailer --------------------------------------------------


def closed_bead_findings(root: Path, beads, gate_from: str | None = None) -> list[str]:
    """Closed beads whose merge carries no `Task:` trailer naming them.

    `beads` is the queue's bead list (id, status) — every bead, not only the
    closed ones, so a merge published ahead of its close is not reported as a
    bead the queue has never heard of — read by the caller so a test never
    reaches the real queue (`tools/test_no_real_queue.py` makes that a property
    of the suite rather than a convention). `gate_from` is the merge commit that
    brought this gate onto `main`: merge commits that are ancestors of it
    predate the trailer and are skipped, not failed.
    """
    known = {bead.get("id") for bead in beads if isinstance(bead, dict)}
    closed = {bead.get("id") for bead in beads
              if isinstance(bead, dict) and bead.get("status") == "closed"}
    all_merges = walk(root, merges_only=True)
    judged = all_merges if gate_from is None else walk(
        root, revision=f"{gate_from}..HEAD", merges_only=True)

    findings: list[str] = []
    for commit in judged:
        bead_id = merge_bead(commit.subject)
        if bead_id is None:
            # Not a bead merge: `Merge branch 'x' of ...` from a plain git
            # merge, which is not this protocol's commit and is not judged.
            continue
        # A bead that is open is not this check's business: the merge is
        # published and the close is a separate, later step, and a gate that
        # failed the gap between them would fail every merge in flight. A bead
        # the queue has never heard of is a different matter — that merge is
        # nobody's work — and is reported once, on its own.
        if bead_id not in known:
            findings.append(
                f"{commit.short} merges {bead_id}, which the queue has no "
                "record of: either the id is wrong or the bead was never "
                "created")
            continue
        if bead_id not in closed:
            continue
        # Two sides, each judged on its own: the merge commit's own trailers,
        # and the trailers on the work it merged. The protocol's trailer is
        # written by whoever does the work, and a merge written by `git merge
        # --no-ff -m` carries a subject and nothing else — judging the merge
        # body alone failed every lane in the repository from 7014ef4 on
        # (SpatialEngine-5ak). Judging them separately rather than as one set
        # is what keeps either half of a mis-dispatch a finding: bead `.4`'s
        # work carrying `.2`'s id is the shape the check exists for, and the
        # merge commit naming `.4` (which `bd-merge-bead.py` composes from the
        # id it was handed) cannot launder it.
        sides = [(commit.trailers, "it"),
                 (work_beads(root, commit.sha), "the work it merged")]
        for named, where in sides:
            if not named:
                continue
            if bead_id not in named:
                findings.append(
                    f"{commit.short} merges {bead_id} but the `Task:` trailers "
                    f"on {where} name {', '.join(dict.fromkeys(named))}: the "
                    "bead and the commit disagree, which is what a "
                    "mis-dispatch looks like after the fact")
        if not any(named for named, _ in sides):
            findings.append(
                f"{commit.short} (Merge {bead_id}) carries no `Task: <id>` "
                "trailer on the merge or on the work it merged: a merge "
                "records the bead it merged, so `git log --grep='Task: <id>'` "
                "finds the work")
    return findings


# --- check 2: the wall -----------------------------------------------------


def wall_findings(root: Path, base: str, head: str = "HEAD") -> list[str]:
    """Wall-touching commits in `base..head` that land with no decision record.

    The range rather than the working tree, because the wall is a property of
    what a commit says it did: a merge cannot launder it, and a change staged
    in a worktree is not yet anybody's decision.
    """
    findings: list[str] = []
    for commit in walk(root, revision=f"{base}..{head}"):
        walls = commit.touches_a_wall()
        if not walls:
            continue
        if commit.changes_a_record() or commit.cites_adr():
            continue
        # Named as the wall rather than as every file under it: a hundred
        # changed files in Spatial.Core is one finding about Spatial.Core.
        touched = ", ".join(sorted({"/".join(path.split("/")[:2]) for path in walls}))
        findings.append(
            f"{commit.short} changes {touched} and neither changes "
            f"{DECISIONS_DIR}*.md nor cites ADR-NNNN in its body: a behaviour "
            "change to a contract or to Core lands its decision record with it")
    return findings


# --- check 3: the lease ----------------------------------------------------


#: The two statuses `paseo` reports for a session that is not over. The gate
#: only *fails* on the first: an agent mid-turn in a bead's worktree with no
#: lease is the double-claim race, while an idle session parked on a finished
#: bead is a coordinator's business and not a lane's — a gate red on the second
#: would be red on the day it landed (ADR-0162).
LIVE_STATUS = "running"
PARKED_STATUS = "idle"


def load_reclaim(root: Path):
    """`tools/bd-safe-reclaim.py` as a module, loaded the way arch-index is.

    The wrapper is the documented fix for the lease race (SpatialEngine-u2x.30)
    and it already answers the only question this check has to ask — is the
    agent holding that bead's branch live? — through two inputs: the worktree
    the agent's cwd is, and the agent id the claim recorded in the notes.
    Reimplementing that here would be a second answer to one question, and the
    second answer is the one nobody runs (the reason checks 4 and 5 are read
    through arch-index rather than written again).
    """
    path = Path(root) / "tools" / "bd-safe-reclaim.py"
    spec = importlib.util.spec_from_file_location("bd_safe_reclaim", path)
    if spec is None or spec.loader is None:
        raise GitError(f"{path}: cannot load tools/bd-safe-reclaim.py")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def _run(cmd):
    return subprocess.run(cmd, capture_output=True, text=True)


def _paseo_json(run, command, what):
    done = run(command)
    if done.returncode != 0:
        detail = (done.stderr or done.stdout or "").strip().splitlines()
        raise GitError(
            f"{what}: {detail[-1] if detail else 'no output'}")
    try:
        return json.loads(done.stdout or "[]")
    except json.JSONDecodeError as error:
        raise GitError(f"{what}: printed no JSON ({error})")


def load_paseo(root: Path, run=None) -> tuple[list[dict] | None, list[dict] | None, str]:
    """The live agents and the bead worktrees, or `(None, None, why)`.

    Same boundary as the queue, for the same reason: `paseo` is a worker's
    machine, not CI's, and a check that could not run has to say so out loud
    because silence and a pass look the same from the outside.
    """
    run = run or _run
    try:
        agents = _paseo_json(run, ["paseo", "ls", "--json"], "paseo ls")
        workspaces = _paseo_json(
            run, ["paseo", "workspace", "ls", "--json"], "paseo workspace ls")
    except (GitError, OSError) as error:
        return None, None, str(error)
    return agents, workspaces, ""


def holds_lease(bead: dict) -> bool:
    """Whether this bead still holds what a claim took.

    A claim writes the assignee and the lease expiry; a reclaim takes both back
    and drops the bead into `bd ready`. So an open bead with neither is one a
    reclaim has been through — or one nobody ever claimed, and the difference
    is decided by `paseo`, not here.
    """
    if str(bead.get("status", "")).lower() == "in_progress":
        return True
    return bool(str(bead.get("assignee") or "").strip()) or bool(
        str(bead.get("lease_expires_at") or "").strip())


def unleased_beads(beads) -> list[dict]:
    """The open beads with no lease, which is where a reclaimed bead lands."""
    return [bead for bead in beads
            if isinstance(bead, dict)
            and str(bead.get("status", "")).lower() != "closed"
            and not holds_lease(bead)]


def _default_protect():
    # The wrapper sits beside this file, so it is loaded from here rather than
    # from the repository under judgement: a fixture repository has no
    # `tools/`, and the question being asked is the wrapper's, not the root's.
    return load_reclaim(Path(__file__).resolve().parent.parent).protect_reason


def _holding(bead, agents, workspaces, status, protect):
    """What holds this bead, counting only agents in one `paseo` status."""
    return protect(bead,
                   [a for a in agents if str(a.get("status", "")).lower() == status],
                   workspaces)


def reclaimed_lease_findings(beads, agents, workspaces, protect=None) -> list[str]:
    """Unleased beads an agent is *running* in whose worktree nobody holds.

    The incident, read mechanically: the lease is gone (a reclaim took it) and
    `paseo` still reports somebody mid-turn in that bead's own worktree. That
    pair is what dropped eight live leases back into `bd ready` in one tick
    (SpatialEngine-u2x.30), where the next tick double-claims work that is
    still being written.

    `protect` is `bd-safe-reclaim.py`'s own `protect_reason`, so "does a live
    agent hold this bead" has one answer in this repository rather than two
    that drift. It is asked of *running* agents only — an idle session parked
    on work it has finished is reported by `parked_lease_holds()` instead,
    because a gate that fails the parked case is red on the day it lands.
    """
    protect = protect or _default_protect()
    findings: list[str] = []
    for bead in unleased_beads(beads):
        held = _holding(bead, agents, workspaces, LIVE_STATUS, protect)
        if not held:
            continue
        findings.append(
            f"{bead.get('id')} has no lease while {held}: that is the shape "
            "of the reclaim race, `bd reclaim` having keyed on lease age alone "
            "(ADR-0162). Read the bead's worktree before touching it, recover "
            "the queue with tools/bd-safe-reclaim.py, and never bare "
            "`bd reclaim`")
    return findings


def parked_lease_holds(beads, agents, workspaces, protect=None) -> list[str]:
    """Unleased beads an *idle* session is still parked in: reported, not gated.

    Same read, same inputs, a session between turns rather than mid-turn. It
    is printed on every run because it is worth a reader — that is how the two
    findings this check found in the repository on the day it landed were
    found — and it is not a finding because the work behind it is usually
    finished or superseded, and a lane that failed it would have been a lane
    nobody could merge from.
    """
    protect = protect or _default_protect()
    held_beads = []
    for bead in unleased_beads(beads):
        if _holding(bead, agents, workspaces, PARKED_STATUS, protect):
            held_beads.append(str(bead.get("id")))
    return held_beads


# --- checks 4 and 5: shared with tools/arch-index.py -----------------------


def load_arch_index(root: Path):
    """`tools/arch-index.py` as a module, loaded the way doc-freshness loads it."""
    path = Path(root) / "tools" / "arch-index.py"
    spec = importlib.util.spec_from_file_location("arch_index", path)
    if spec is None or spec.loader is None:
        raise GitError(f"{path}: cannot load tools/arch-index.py")
    module = importlib.util.module_from_spec(spec)
    # Registered before it runs: arch-index's `@dataclass` reads
    # `sys.modules[cls.__module__]`, and a module loaded without an entry there
    # raises while the class body executes rather than at the call site.
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def shared_adr_findings(root: Path) -> dict[str, list[str]]:
    """Checks 4 and 5, read through arch-index's own entry points.

    The bead's own instruction: implement once, have both call it. G1/G2 landed
    that implementation and its gate (`arch-index.py --check`, which every lane
    runs), so this reads it rather than growing a second answer to "does ADR-0141
    exist" — and `tools/test_beads_gate.py` pins that the two agree.
    """
    arch = load_arch_index(root)
    corpus = arch.load_corpus(Path(root))
    return {
        "citations": arch.dangling_citations(Path(root), corpus),
        "register": arch.register_findings(Path(root), corpus),
        "corpus": arch.corpus_findings(Path(root), corpus),
    }


# --- the queue -------------------------------------------------------------


def load_beads(root: Path, db: str | None = None) -> tuple[list[dict] | None, str]:
    """The queue's bead list, or `(None, why)` when the queue cannot be read.

    Every bead, open ones included: check 1 asks whether the queue has heard of
    the bead a merge names, and it has — a merge is published before the close
    is a separate, later step, so reading only the closed set reported a real,
    in-flight bead as one the queue had "no record of" and failed every lane in
    the repository (7014ef4, SpatialEngine-5ak).

    The queue is local coordination state in the shared git dir, so it is there
    on a worker's machine and absent in CI. The second element of the tuple is
    printed rather than swallowed: a check that could not run has to say so,
    because silence and a pass look the same from the outside.
    """
    command = ["bd", "list", "--all", "--json", "-n", "0"]
    if db:
        command += ["--db", db]
    try:
        done = subprocess.run(command, cwd=root, capture_output=True, text=True)
    except OSError as error:
        return None, f"bd could not be run ({error})"
    if done.returncode != 0:
        detail = (done.stderr or done.stdout or "").strip().splitlines()
        return None, f"bd list --status closed failed: {detail[-1] if detail else 'no output'}"
    try:
        payload = json.loads(done.stdout or "[]")
    except json.JSONDecodeError as error:
        return None, f"bd list --status closed printed no JSON ({error})"
    return payload, ""


def read_beads_file(path: str) -> list[dict]:
    """A bead list from a file, which is how a test and a rehearsal supply one."""
    return json.loads(Path(path).read_text(encoding="utf-8"))


def gate_from(root: Path) -> str | None:
    """The merge commit that first brought this gate onto the branch.

    The shape rule's precedent (ADR-0150): a rule applies from the number or
    the commit that decided it, and a corpus written under no rule is
    grandfathered rather than rewritten. Read as "the commit that added this
    file", which on the branch that adds it is the gate's own commit and on
    `main` is the merge that carried it.
    """
    out = git(root, "log", "--diff-filter=A", "--format=%H", "--",
              "tools/beads_gate.py").strip()
    lines = [line for line in out.splitlines() if line.strip()]
    return lines[-1] if lines else None


# --- the gate --------------------------------------------------------------


def judge(root: Path, base: str, head: str = "HEAD", beads=None,
          gate_from_ref: str | None = None, adr: bool = False,
          agents=None, workspaces=None) -> dict:
    """Every finding, and the counts a reader needs to trust the scope."""
    result: dict = {"findings": [], "scope": {}}
    result["findings"].extend(wall_findings(root, base=base, head=head))
    result["scope"]["wall_commits"] = len(walk(root, revision=f"{base}..{head}"))

    if beads is None:
        result["scope"]["trailer"] = (
            "not judged: no bead queue to read (the queue is local "
            "coordination state, and this run has none --strict would fail)")
    else:
        watermark = gate_from_ref if gate_from_ref is not None else gate_from(root)
        result["findings"].extend(
            closed_bead_findings(root, beads=beads, gate_from=watermark))
        all_merges = len(walk(root, merges_only=True))
        judged = all_merges if watermark is None else len(
            walk(root, revision=f"{watermark}..HEAD", merges_only=True))
        result["scope"]["trailer"] = (
            f"{judged} merge commit(s) judged"
            + ("" if watermark is None else
               f", {all_merges - judged} grandfathered (merged before "
               f"{watermark[:7]}, which carried this gate)"))

    if agents is None or workspaces is None or beads is None:
        result["scope"]["lease"] = (
            "not judged: the queue and paseo are the two halves of it, and "
            "this run has neither (both are a worker's machine, not CI's "
            "--strict would fail)")
    else:
        unleased = unleased_beads(beads)
        result["findings"].extend(
            reclaimed_lease_findings(beads, agents, workspaces))
        parked = parked_lease_holds(beads, agents, workspaces)
        result["scope"]["lease"] = (
            f"{len(unleased)} unleased bead(s) read against {len(agents)} "
            f"paseo agent(s)"
            + ("" if not parked else
               f"; {len(parked)} held by an idle session and reported rather "
               f"than failed ({', '.join(parked)})"))

    if adr:
        shared = shared_adr_findings(root)
        for name, findings in shared.items():
            result["findings"].extend(f"ADR {name}: {finding}" for finding in findings)
        result["scope"]["adr"] = ", ".join(
            f"{name}: {len(findings)}" for name, findings in shared.items())
    else:
        result["scope"]["adr"] = (
            "shared with tools/arch-index.py --check, which every lane gates on")
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=".", help="repository root (default: .)")
    parser.add_argument("--base", default="origin/main",
                        help="the change set's base (default: origin/main)")
    parser.add_argument("--head", default="HEAD", help="the tip to judge")
    parser.add_argument("--beads", metavar="FILE",
                        help="read the bead list (id, status) from a JSON "
                             "file instead of the queue")
    parser.add_argument("--db", metavar="PATH",
                        help="a different beads queue (a rehearsal, a test)")
    parser.add_argument("--no-queue", action="store_true",
                        help="do not read the queue at all: check 1 is "
                             "reported as not judged rather than read")
    parser.add_argument("--no-paseo", action="store_true",
                        help="do not read the live agents at all: check 3 is "
                             "reported as not judged rather than read")
    parser.add_argument("--watermark", metavar="REF",
                        help="the commit this gate arrived on; merge commits "
                             "before it are grandfathered rather than judged")
    parser.add_argument("--adr", action="store_true",
                        help="also gate on the shared ADR citation and "
                             "register reads (otherwise reported)")
    parser.add_argument("--strict", action="store_true",
                        help="fail when the bead queue cannot be read")
    parser.add_argument("--json", action="store_true",
                        help="machine-readable output")
    parser.add_argument("--plan", action="store_true",
                        help="print what would be judged, and judge nothing")
    arguments = parser.parse_args(argv)
    root = Path(arguments.root).resolve()

    try:
        if arguments.plan:
            print(f"beads-gate: would judge {arguments.base}..{arguments.head} "
                  f"for the ADR wall, and every merge commit after the gate's "
                  f"watermark for a `Task: <id>` trailer naming the bead it "
                  "merged, on the merge or on the work it brought in")
            if arguments.adr:
                print("beads-gate: would also gate the shared ADR citation and "
                      "register reads")
            if not arguments.no_paseo:
                print("beads-gate: would read every unleased bead against the "
                      "live paseo agents and report one whose lease went while "
                      "its agent was still running")
            return 0

        beads: list[dict] | None = None
        queue_note = ""
        if arguments.beads:
            beads = read_beads_file(arguments.beads)
        elif not arguments.no_queue:
            beads, queue_note = load_beads(root, arguments.db)

        agents = workspaces = None
        paseo_note = ""
        if not arguments.no_paseo:
            agents, workspaces, paseo_note = load_paseo(root)

        result = judge(root, base=arguments.base, head=arguments.head,
                       beads=beads, gate_from_ref=arguments.watermark,
                       adr=arguments.adr, agents=agents, workspaces=workspaces)
    except (GitError, OSError) as error:
        print(f"beads-gate: {error}", file=sys.stderr)
        return 2

    findings = list(result["findings"])
    for note, what in ((queue_note, "bead queue"), (paseo_note, "paseo")):
        if note and arguments.strict:
            findings.append(f"{what}: {note}")
    result["findings"] = findings

    if arguments.json:
        print(json.dumps(result, indent=2, sort_keys=True))
    else:
        for name, text in sorted(result["scope"].items()):
            print(f"beads-gate: {name}: {text}")
        if queue_note:
            print(f"beads-gate: queue: {queue_note}")
        if paseo_note:
            print(f"beads-gate: paseo: {paseo_note}")
        for finding in findings:
            print(f"  {finding}", file=sys.stderr)
        if findings:
            print(f"beads-gate: {len(findings)} finding(s)", file=sys.stderr)
            return 1
        print("beads-gate: the bead protocol and the ADR wall are clean")
    return 1 if findings else 0


if __name__ == "__main__":
    raise SystemExit(main())
