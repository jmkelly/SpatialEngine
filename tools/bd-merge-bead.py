#!/usr/bin/env python3
"""Merge a bead into main, publish it, and only then close the bead.

    python3 tools/bd-merge-bead.py --check                 # is origin/main caught up?
    python3 tools/bd-merge-bead.py --publish               # push local main
    python3 tools/bd-merge-bead.py --bead <id>             # merge, push, close
    python3 tools/bd-merge-bead.py --audit                 # closed beads not on origin/main

The swarm lost completed work twice over, and both losses were the merge step
being prose instead of a command (SpatialEngine-xbz):

  * **close before merge.** `bd close` was run on the strength of a green
    `eng/verify.sh` on the *branch*. A branch-only green gate proves nothing
    about the merge, so the bead went closed while its commit still sat on
    `bd/<id>` — nobody was on the bead any more, and the work was stranded.
  * **merge without push.** A merge landed in local `main` and was never
    pushed. `origin/main` — the base every worker is branched off
    (`paseo workspace create --base origin/main`) — stayed behind, so new work
    started from a base missing everything already merged, and a later tick
    reading `origin/main` concluded the work was unmerged and re-merged it.

So the gate here is the ancestor check, and it names `origin/main`, never local
main: after the push, `git merge-base --is-ancestor <merge> origin/main` must
pass before `bd close` runs. Everything upstream of that (fetch, rebase onto
`origin/main`, verify, merge, push) is in this tool too, so the step a
coordinator performs is the step that is checked.

The verify step runs `eng/verify.sh --fast` — the scoped build gate: a build of
the projects the change reaches and the tests that reach it. That is the merge
gate under ADR-0134, which amended ADR-0118's rule that the merge runs `--full`.
`--full` costs 15–25 minutes per merge and the swarm merges hundreds of times a
day, so it was the price of every merge rather than a signal; CI on `main` and
an occasional `--bead --full` still run the exhaustive gate, and `--verified`
still lets an operator declare one they ran by hand.

What this tool keeps, because it costs seconds and it is the loss this tool
exists to prevent: the fetch before the rebase, the refusal to merge into an
unpublished local `main`, the red-gate abort before any merge, the publish gate,
and the ancestor check that has to pass before `bd close` runs. ADR-0118's
warning still stands for any *other* reader of these commands: under it a bare
`eng/verify.sh` is the build gate, so the lane is always named here — never
bare, and `--fast` rather than `--full` because the merge gate is the scoped
lane.

Deliberate refusals, because a gate that can be talked past is not a gate:

  * a **red** verify lane aborts before the merge;
  * an **interrupted** lane aborts the same way — a cancelled run is not a gate
    result, and the tool merges nothing on its way out;
  * a **rebase conflict** aborts (the runbook escalates a non-trivial one);
  * a **local `main` that `origin/main` does not have** aborts: merging into an
    unpublished `main` is how the queue drifts, so publish it first with
    `--publish` (which refuses to force, unless `--allow-rewrite` is passed for
    the case where this run rebased an already-merged branch);
  * a **failed push** aborts. The bead stays open and the commit stays in local
    `main` for a human, which is recoverable; a closed bead on an unpushed
    branch is not.

`--full` runs the exhaustive lane instead of the fast one, for a change broad
enough to doubt the scoping. `--skip-tests <substring>` (repeatable) leaves a
named suite to CI on this merge — the container-backed suites cost minutes each
and CI runs them on `main` regardless — and writes that into the `bd close`
reason, so a merge that leaned on CI says so in the record. `--verified` (spelled
`--full-verified` in older runs) declares that the operator already ran the lane
green on this rebased commit — the re-run after a failed push, which would
otherwise pay for the gate twice for one merge. It skips the run and nothing
else: the publish gate, the ancestor check and the close all still apply, and
the fact is recorded in the reason `bd close` writes.
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path

# The ref the swarm branches workers off and the only ref a close may be
# justified against: local `main` is one worktree's opinion, `origin/main` is
# what every future worker will start from.
PUBLISHED_REF = "origin/main"

LOCAL_REF = "main"

# The merge gate's verify lane: the scoped build gate, named so a runner's
# CI=true cannot quietly promote a merge to the 20-minute `--full` (ADR-0134).
# `eng/verify.sh` with no arguments is the same lane; naming it is what keeps
# ADR-0118's CI clause from deciding what a merge costs.
FAST_LANE = "--fast"

# The exhaustive lane, for `--bead --full` and for the operator's own runs.
FULL_LANE = "--full"

# An interrupted long run is not a gate result: 130 is what a shell reports for
# a signalled command, and the coordinator's tick keys on non-zero.
CANCELLED = 130

# A commit sha recorded in a bead's notes, as the hand-off rule tells a worker
# to write it: "<sha> bd/<id>".
NOTES_COMMIT = re.compile(r"\b(?P<sha>[0-9a-fA-F]{7,40})\b")


class Completed:
    """The slice of subprocess.CompletedProcess this tool reads."""

    def __init__(self, returncode, stdout, stderr):
        self.returncode = returncode
        self.stdout = stdout
        self.stderr = stderr


def _default_run(cmd, cwd=None):
    return subprocess.run(cmd, capture_output=True, text=True, cwd=cwd)


def _detail(result):
    return (result.stderr.strip() or result.stdout.strip()
            or f"exit {result.returncode}")


def _ok(run, cmd, what, cwd=None):
    result = run(cmd, cwd=cwd)
    if result.returncode != 0:
        raise RuntimeError(f"{what} failed: {_detail(result)}")
    return result


def _fail(run, cmd, cwd=None):
    return run(cmd, cwd=cwd).returncode == 0


def commits_ahead(run, local= LOCAL_REF, remote=PUBLISHED_REF, cwd=None):
    """The commits `local` has that `remote` does not, oldest first."""
    result = _ok(run, ["git", "log", "--format=%H %s", f"{remote}..{local}"],
                 f"git log {remote}..{local}", cwd)
    return [line.strip() for line in (result.stdout or "").splitlines()
            if line.strip()]


def _known_commit(run, sha, cwd=None):
    """Whether this repository has that commit at all."""
    return _fail(run, ["git", "rev-parse", "--verify", "--quiet",
                       f"{sha}^{{commit}}"], cwd)


def is_published(run, sha, cwd=None):
    """Whether `sha`'s work is on `origin/main`. The close gate.

    Ancestry first, because that is the check the bug report asks for: a close
    justified by "it is on local main" is what stranded the work. Then
    patch-equivalence, because a rebase legitimately rewrites the sha and a
    naive ancestry check reads every rebased branch as lost work.
    """
    return verdict_for(run, sha, cwd) in ("published", "rebased")


def verdict_for(run, sha, cwd=None):
    """`published`, `rebased` (present under a new sha), `unknown`, `stranded`."""
    if _fail(run, ["git", "merge-base", "--is-ancestor", sha, PUBLISHED_REF],
             cwd):
        return "published"
    if not _known_commit(run, sha, cwd):
        return "unknown"
    result = run(["git", "cherry", "-v", PUBLISHED_REF, sha], cwd=cwd)
    if result.returncode != 0:
        return "unknown"
    lines = [line for line in (result.stdout or "").splitlines() if line.strip()]
    if lines and all(line.startswith("-") for line in lines):
        return "rebased"
    return "stranded"


def notes_commits(bead):
    """Commit shas a bead's notes record, longest first, deduplicated."""
    found = []
    for match in NOTES_COMMIT.finditer(bead.get("notes") or ""):
        sha = match.group("sha")
        if sha.lower() not in [f.lower() for f in found]:
            found.append(sha)
    return sorted(found, key=len, reverse=True)


def worktree_for_branch(run, branch, cwd=None):
    """Where this branch is checked out, or None when it is nowhere.

    A bead's branch lives in its own worktree, and git refuses to rebase a
    branch that is checked out elsewhere — so the merge step has to run the
    rebase and the verify gate in that worktree, and only the merge and the
    push in the repo that owns `main`.
    """
    result = run(["git", "worktree", "list", "--porcelain"], cwd=cwd)
    if result.returncode != 0:
        return None
    path = None
    for line in (result.stdout or "").splitlines():
        if line.startswith("worktree "):
            path = line[len("worktree "):].strip()
        elif line.startswith("branch ") and line[len("branch "):].strip().endswith(
                f"/{branch}") and path:
            return path
    return None


def _json_output(run, cmd, what):
    result = _ok(run, cmd, what)
    return json.loads(result.stdout or "[]")


def check_published(run, emit, cwd=None):
    """`--check`: is everything merged published? The top-of-tick gate."""
    if not _ok(run, ["git", "rev-parse", "--verify", PUBLISHED_REF],
               "git rev-parse origin/main", cwd):
        return 1
    ahead = commits_ahead(run, cwd=cwd)
    if ahead:
        emit(f"unpublished: {len(ahead)} commit(s) on {LOCAL_REF} that "
             f"{PUBLISHED_REF} does not have:")
        for line in ahead:
            emit(f"  {line}")
        emit("publish it: python3 tools/bd-merge-bead.py --publish")
        return 1
    emit(f"published: {LOCAL_REF} is at {PUBLISHED_REF}")
    return 0


def publish(run, emit, allow_rewrite=False, cwd=None):
    """`--publish`: push local main. Fast-forward only unless forced."""
    if not _ok(run, ["git", "rev-parse", "--verify", PUBLISHED_REF],
               "git rev-parse origin/main", cwd):
        return 1
    ahead = commits_ahead(run, cwd=cwd)
    if not ahead:
        emit(f"nothing to publish; {LOCAL_REF} is at {PUBLISHED_REF}")
        return 0
    cmd = ["git", "push", "origin", LOCAL_REF]
    if allow_rewrite:
        cmd.insert(2, "--force-with-lease")
    result = run(cmd, cwd=cwd)
    if result.returncode != 0:
        emit(f"publish failed: {_detail(result)}")
        if not allow_rewrite:
            emit("if this run rebased a branch that was already merged, "
                 "re-run with --allow-lease")
        return 1
    emit(f"published {len(ahead)} commit(s) to {PUBLISHED_REF}")
    return 0


def audit(run, emit, cwd=None):
    """`--audit`: closed beads whose recorded work is not on origin/main.

    This is the detector for the incident itself: a bead closed without its
    commit published, found after the fact rather than guessed at.

    Two verdicts are deliberately *not* failures, because a gate that cries
    wolf is a gate the coordinator learns to skip: `rebased` (the commit is not
    an ancestor but an equivalent patch is on `origin/main` — the rebase
    rewrote the sha, the work is there) and `unknown` (the sha is not in this
    repository at all, so there is nothing to check and nothing is claimed).

    `stranded` is exact and narrow: this commit, or a patch identical to it, is
    not on `origin/main`. It is a triage list, not proof of loss — a merge
    whose content was amended on the way in (an ADR renumbered, say) has a
    different patch-id and reads as stranded. Read the branch before creating a
    recovery bead.
    """
    beads = _json_output(run, ["bd", "list", "--status", "closed", "--json",
                               "-n", "0"], "bd list --status closed")
    stranded, tolerated = [], []
    for bead in beads:
        for sha in notes_commits(bead):
            verdict = verdict_for(run, sha, cwd)
            if verdict == "stranded":
                stranded.append((bead.get("id", ""), sha))
            elif verdict in ("rebased", "unknown"):
                tolerated.append((bead.get("id", ""), sha, verdict))
    for bead_id, sha in stranded:
        emit(f"stranded: {bead_id} is closed but {sha} is not on "
             f"{PUBLISHED_REF}, nor an equivalent patch of it")
    for bead_id, sha, verdict in tolerated:
        emit(f"ok ({verdict}): {bead_id} {sha}")
    if stranded:
        emit(f"{len(stranded)} closed bead(s) record a commit that is not on "
             f"{PUBLISHED_REF}: for each, check the branch and the commits "
             f"around it before re-merging (an amended or renumbered merge "
             f"also reads as stranded)")
        return 1
    emit(f"audit: no closed bead is missing work from {PUBLISHED_REF} "
         f"({len(tolerated)} recorded commit(s) absent by sha, all accounted "
         f"for)")
    return 0


def merge_bead(run, emit, bead_id, branch=None, reason=None, full_lane=False,
               allow_lease=False, skipped_lane=False, skip_tests=(), cwd=None):
    """`--bead <id>`: the whole merge step, ending in a justified close."""
    branch = branch or f"bd/{bead_id}"
    tree = worktree_for_branch(run, branch, cwd) or cwd
    lane = FULL_LANE if full_lane else FAST_LANE
    if tree != cwd:
        emit(f"rebasing and verifying in the bead's worktree {tree}")
    try:
        # A close may only be justified against a freshly fetched origin: an
        # ancestor check against a stale origin/main is the bug this replaces.
        _ok(run, ["git", "fetch", "origin"], "git fetch origin", cwd)
        _ok(run, ["git", "rev-parse", "--verify", PUBLISHED_REF],
            "git rev-parse origin/main", cwd)

        ahead = commits_ahead(run, cwd=cwd)
        if ahead:
            emit(f"refusing to merge: {len(ahead)} commit(s) on {LOCAL_REF} "
                 f"that {PUBLISHED_REF} does not have "
                 f"({ahead[0].split(' ', 1)[0]})")
            emit("publish first: python3 tools/bd-merge-bead.py --publish")
            return 1

        _ok(run, ["git", "rebase", PUBLISHED_REF, branch],
            f"git rebase {PUBLISHED_REF} {branch} "
            f"(conflict: escalate, do not resolve silently)", tree)

        if skipped_lane:
            emit(f"skipping the gate: eng/verify.sh {lane} was declared "
                 f"green on this rebased commit (--verified)")
        else:
            emit(f"verifying the rebased branch: eng/verify.sh {lane}")
            verify = [str(Path(tree) / "eng" / "verify.sh"), lane]
            verify += [f"--skip-tests={pattern}" for pattern in skip_tests]
            _ok(run, verify,
               f"eng/verify.sh {lane} (red gate: the bead stays open)", tree)

        subject = _ok(run, ["git", "log", "-1", "--format=%s", branch],
                      "git log", cwd).stdout.strip() or bead_id
        _ok(run, ["git", "checkout", LOCAL_REF], "git checkout main", cwd)
        _ok(run, ["git", "merge", "--no-ff", "-m",
                  f"Merge {bead_id}: {subject}", branch],
            f"git merge {branch}", cwd)
        merge_sha = _ok(run, ["git", "rev-parse", LOCAL_REF],
                        "git rev-parse main", cwd).stdout.strip()

        pushed = run(["git", "push", "origin", LOCAL_REF], cwd=cwd)
        if pushed.returncode != 0:
            emit(f"merge {merge_sha[:7]} is in {LOCAL_REF} but the push "
                 f"failed: {_detail(pushed)}")
            emit(f"{bead_id} left OPEN; nothing was lost, but nothing is "
                 f"published either. Push, then re-run this command.")
            if not allow_lease:
                emit("if this run rebased a branch that was already merged, "
                     "re-run with --allow-lease")
            return 1
        emit(f"published {merge_sha[:7]} to {PUBLISHED_REF}")

        # Best effort: a recovery bead needs origin/<branch> to fetch from.
        run(["git", "push", "--force-with-lease", "origin", branch], cwd=cwd)

        if not is_published(run, merge_sha, cwd):
            emit(f"refusing to close {bead_id}: {merge_sha[:7]} is not an "
                 f"ancestor of {PUBLISHED_REF}. The bead stays open.")
            return 1
    except KeyboardInterrupt:
        # A cancelled verify lane is not a green one. Say so, merge nothing,
        # close nothing, and let the branch stand for a re-run.
        emit(f"{bead_id} not merged: eng/verify.sh was interrupted "
             f"(Ctrl-C), which is not a gate result. The bead stays open; "
             f"re-run this command.")
        return CANCELLED
    except RuntimeError as error:
        emit(f"{bead_id} not merged: {error}")
        return 1

    run(["bd", "label", "remove", "needs-merge", bead_id], cwd=cwd)
    close_reason = reason or f"merged and published as {merge_sha[:7]}"
    close_reason += f"; eng/verify.sh {lane} green on the rebased commit"
    if skip_tests:
        close_reason += (f"; CI-covered suites skipped by name "
                         f"({', '.join(skip_tests)}) and caught on main instead")
    if skipped_lane:
        close_reason += " (declared green by the operator, not run here)"
    close = run(["bd", "close", bead_id, "--reason", close_reason], cwd=cwd)
    if close.returncode != 0:
        emit(f"work is published but closing {bead_id} failed: "
             f"{_detail(close)}")
        return 1
    emit(f"closed {bead_id}: {merge_sha[:7]} is on {PUBLISHED_REF}")
    return 0


def main(argv=None, run=None, out=None, cwd=None):
    run = run or _default_run
    emit = (lambda line: print(line, file=out or sys.stdout))
    cwd = cwd or str(Path(__file__).resolve().parent.parent)

    parser = argparse.ArgumentParser(
        prog="bd-merge-bead",
        description="Merge, publish, and only then close a bead.")
    parser.add_argument("--bead", metavar="ID", default=None,
                        help="merge this bead's branch, push, then close it")
    parser.add_argument("--branch", default=None,
                        help="branch to merge (default bd/<bead>)")
    parser.add_argument("--reason", default=None,
                        help="reason recorded by bd close")
    parser.add_argument("--check", action="store_true",
                        help="fail when local main is ahead of origin/main")
    parser.add_argument("--publish", action="store_true",
                        help="push local main to origin")
    parser.add_argument("--audit", action="store_true",
                        help="report closed beads whose work is not on origin/main")
    parser.add_argument("--allow-lease", action="store_true",
                        help="permit a force-with-lease push when rebasing "
                             "a branch that was already merged")
    parser.add_argument("--full", action="store_true",
                        help="gate the merge on the exhaustive lane "
                             "(eng/verify.sh --full, 15-25 min) instead of the "
                             "fast scoped one; for a change broad enough to "
                             "doubt the scoping")
    parser.add_argument("--skip-tests", metavar="SUBSTRING", action="append",
                        default=[],
                        help="do not run the test projects whose path contains "
                             "SUBSTRING on this merge; repeatable. For the "
                             "container-backed suites, which cost minutes each "
                             "and are covered by CI on main either way. "
                             "Recorded in the close reason, so a merge that "
                             "leaned on CI says so")
    parser.add_argument("--verified", "--full-verified", dest="verified",
                        action="store_true",
                        help="skip the verify lane because it was already "
                             "green on this rebased commit (a re-run after a "
                             "failed push); the publish gate and the close "
                             "still apply")
    args = parser.parse_args(argv)

    if args.check:
        return check_published(run, emit, cwd)
    if args.publish:
        return publish(run, emit, allow_rewrite=args.allow_lease, cwd=cwd)
    if args.audit:
        return audit(run, emit, cwd)
    if not args.bead:
        parser.error("nothing to do: pass --bead, --check, --publish or --audit")
    return merge_bead(run, emit, args.bead, branch=args.branch,
                      reason=args.reason, full_lane=args.full,
                      allow_lease=args.allow_lease,
                      skipped_lane=args.verified,
                      skip_tests=tuple(args.skip_tests), cwd=cwd)


if __name__ == "__main__":
    sys.exit(main())
