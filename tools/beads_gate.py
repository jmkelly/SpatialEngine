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

Prose that has already failed is advice. So four *mechanical* checks live here,
with no judgement in any of them:

  1. **the trailer.** A closed bead's merge commit carries `Task: <id>`, and the
     id it names is the bead the commit merges. This is what a mis-dispatch
     looks like read mechanically — the bead and the trailer disagree — so both
     halves are checked, and a merge of a bead the queue does not have is
     reported too.

  2. **the wall.** A commit touching `src/Spatial.Contracts/**` or
     `src/Spatial.Core/**` changes an ADR, or cites `ADR-NNNN` in its body:
     `AGENTS.md`'s "behaviour changes land contract, SDK, test and ADR updates
     together", as a comparison rather than a review. Only those two
     directories are walls; a host change with no record is ordinary work.

  3. **the citation**, and 4. **the register**: G1/G2's, implemented once in
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
#: commit a bead's merge commit at all.
MERGE_SUBJECT = re.compile(r"^Merge (?P<id>[A-Za-z0-9][\w.-]*): ")

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


# --- check 1: the trailer --------------------------------------------------


def closed_bead_findings(root: Path, beads, gate_from: str | None = None) -> list[str]:
    """Merge commits that do not carry their own bead's `Task:` trailer.

    `beads` is the queue's closed-bead list (id, status), read by the caller so
    a test never reaches the real queue — `tools/test_no_real_queue.py` makes
    that a property of the suite rather than a convention. `gate_from` is the
    merge commit that brought this gate onto `main`: merge commits that are
    ancestors of it predate the trailer and are skipped, not failed.
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
        trailers = commit.trailers
        # A bead that is open is not this check's business: the merge is
        # published and the close is a separate, later step, and a gate that
        # failed the gap between them would fail every merge in flight. A bead
        # the queue has never heard of is a different matter — that merge is
        # nobody's work.
        if bead_id not in known:
            findings.append(
                f"{commit.short} merges {bead_id}, which the queue has no "
                "record of: either the id is wrong or the bead was never "
                "created")
        elif bead_id not in closed:
            continue
        if not trailers:
            findings.append(
                f"{commit.short} (Merge {bead_id}) carries no `Task: <id>` "
                "trailer: a merge commit records the bead it merged, so "
                "`git log --grep='Task: <id>'` finds the work")
        elif bead_id not in trailers:
            findings.append(
                f"{commit.short} merges {bead_id} but its `Task:` trailer names "
                f"{', '.join(trailers)}: the bead and the commit disagree, "
                "which is what a mis-dispatch looks like after the fact")
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


# --- checks 3 and 4: shared with tools/arch-index.py -----------------------


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
    """Checks 3 and 4, read through arch-index's own entry points.

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
    """The closed-bead list, or `(None, why)` when the queue cannot be read.

    The queue is local coordination state in the shared git dir, so it is there
    on a worker's machine and absent in CI. The second element of the tuple is
    printed rather than swallowed: a check that could not run has to say so,
    because silence and a pass look the same from the outside.
    """
    command = ["bd", "list", "--status", "closed", "--json", "-n", "0"]
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
          gate_from_ref: str | None = None, adr: bool = False) -> dict:
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
                        help="read the closed-bead list from a JSON file "
                             "instead of the queue")
    parser.add_argument("--db", metavar="PATH",
                        help="a different beads queue (a rehearsal, a test)")
    parser.add_argument("--no-queue", action="store_true",
                        help="do not read the queue at all: check 1 is "
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
                  f"watermark for `Task: <id>`")
            if arguments.adr:
                print("beads-gate: would also gate the shared ADR citation and "
                      "register reads")
            return 0

        beads: list[dict] | None = None
        queue_note = ""
        if arguments.beads:
            beads = read_beads_file(arguments.beads)
        elif not arguments.no_queue:
            beads, queue_note = load_beads(root, arguments.db)

        result = judge(root, base=arguments.base, head=arguments.head,
                       beads=beads, gate_from_ref=arguments.watermark,
                       adr=arguments.adr)
    except (GitError, OSError) as error:
        print(f"beads-gate: {error}", file=sys.stderr)
        return 2

    findings = list(result["findings"])
    if queue_note and arguments.strict:
        findings.append(f"bead queue: {queue_note}")
    result["findings"] = findings

    if arguments.json:
        print(json.dumps(result, indent=2, sort_keys=True))
    else:
        for name, text in sorted(result["scope"].items()):
            print(f"beads-gate: {name}: {text}")
        if queue_note:
            print(f"beads-gate: queue: {queue_note}")
        for finding in findings:
            print(f"  {finding}", file=sys.stderr)
        if findings:
            print(f"beads-gate: {len(findings)} finding(s)", file=sys.stderr)
            return 1
        print("beads-gate: the bead protocol and the ADR wall are clean")
    return 1 if findings else 0


if __name__ == "__main__":
    raise SystemExit(main())
