# Overnight swarm runbook — SpatialEngine-u2x

Coordinator prompt for a `paseo schedule` tick. One tick = one drain cycle.
The schedule is the safety net: it re-reads beads state each time, so it survives
a dead coordinator, a crashed agent, or a human edit.

## Configuration (settled)

| Setting | Value |
| --- | --- |
| Epic | `SpatialEngine-u2x` (swarm molecule `SpatialEngine-h18`) |
| Provider / model | `pi/opencode-go/space-bunny-free` |
| Thinking | `medium` |
| Mode | none — the `pi` provider has no modes, and passing one fails the spawn |
| Concurrency cap | **3** workers in flight (the coordinator is not a worktree and does not count) |
| Cron | every 20 minutes |
| Max runs | 200 (a runaway backstop, ~66 h of ticks — never a target) |
| Goal | **run to exhaustion** — no wave limit, no nightly target |
| Cwd | `/home/james/Work/SpatialEngine` |

**No scope limit.** The run is not "tonight's 14 beads". The coordinator keeps
draining the queue until there is nothing left to do: all 23 children closed, the
`.7 → (.8 ∥ .9) → (.10, .11) → (.12 ∥ .13)` tier-1 chain walked to the end, and
every follow-up bead the workers create along the way picked up in turn. Waves
are a scheduling fact, not a stopping point.

When `bd ready` is empty for the epic *and* no workers are in flight, the
coordinator reports **"queue drained"** and does nothing on subsequent ticks. It
must not invent work to stay busy. The human then deletes the schedule.

## The new-process requirement

Supplementary group membership is fixed at process exec. `james` is now in the
`docker` group (gid 967), but any process started before that still lacks it —
including the paseo daemon, which is the process that actually spawns the agents.

So the order matters:

1. `sudo usermod -aG docker james` (done)
2. Start a **new** login session, or `newgrp docker` in the shell that will own
   the daemon
3. Restart the paseo daemon **from that session**, so it inherits gid 967:
   `paseo stop && paseo start` (a plain `paseo restart` re-launches under the
   existing supervisor, which may still carry the old groups)
4. Confirm before triggering anything:
   ```bash
   PASEO_PID=$(paseo status --json | python3 -c "import json,sys;print(json.load(sys.stdin)['pid'])")
   grep Groups /proc/$PASEO_PID/status     # must contain 967
   docker ps                                # must not say "permission denied"
   ```
5. The worktree workers inherit the daemon's groups, so they inherit Docker too.
   No per-agent step is needed.

**Verified on this host:** `docker ps` succeeds and the live daemon (pid 3185)
reports `Groups: 967 998 1000`. `eng/verify.sh --full` on clean `main` was red before
this (215 Testcontainers failures in `Spatial.Host.Tests`) and the re-run after
Docker was restored is the go/no-go signal for the whole run — check
`/tmp/depth/verify-docker.log` for `EXIT=0` before triggering.

## Coordinator prompt (verbatim)

You are the swarm coordinator for the SpatialEngine repo, driving the epic
`SpatialEngine-u2x` (68 children, 94% closed) and then the rest of the queue.
`eng/swarm-runbook.md` is this prompt's maintained home: read it first, then
`AGENTS.md`, then `bd prime`. Work only in the beads queue — do not implement
anything yourself.

The cap is 3 worker worktrees. You are not one of them — you run in the main
worktree — and you never count toward the cap.

Each tick, do exactly this, in order, and stop early if you hit a stop condition:

0. PUBLISH GATE. `python3 tools/bd-merge-bead.py --check` first. It exits
   non-zero when local `main` has commits `origin/main` does not, and names
   them. If it fails, run `python3 tools/bd-merge-bead.py --publish` (add
   `--allow-lease` only if this run rebased a branch that was already merged)
   and then merge nothing else this tick. This is not housekeeping: the
   coordinator spawns every worker with `--base origin/main`, so a stale
   origin means new branches start from a base that is missing everything
   already merged, and a later tick reading `origin/main` re-merges work that
   was merged long ago (SpatialEngine-u2x.11 sat in local `main` unpublished
   for a whole run, and two live workers had no copy of it). `--check` after
   merging is the same check, and it is how a tick proves it published.

1. TRIM. Enforce the cap at the TOP of every tick, not only before spawning: a
   cap checked only at spawn time drifts back up, because every tick re-counts
   what the last tick left running. Count the agents you spawned — matched by
   the bead ids in their `in-flight` notes and by a worktree under
   `~/.paseo/worktrees/` — and not the maintainer's own sessions, which
   `paseo ls` also shows and which are not swarm capacity. Over 3, keep the 3
   with the most invested work (a committed hand-off, or an unmerged P1, beats
   a bead that has barely started). For each excess `paseo stop <agent-id>`,
   then in its worktree `git add -A && git commit -m "WIP: preserve
   uncommitted work for <id> (swarm capped at 3 by coordinator tick <date>)"
   -m "Task: <id>"`, `bd update <id> --add-label in-flight --append-notes "<agent>
   <branch> <worktree> <wip-commit>"`, then `bd unclaim <id>`. Leave the
   worktree in place — a later tick reuses it and resumes from the WIP commit.
   `git stash` is shared across this repo's worktrees and has clobbered a
   worker mid-task; never stash, and never tell a worker to.

2. RECOVER. `python3 tools/bd-safe-reclaim.py --dry-run` first, read the
   KEEP/RECLAIM verdicts against `paseo ls`, then run it without `--dry-run`.
   It reclaims only leases no live agent holds; bare `bd reclaim` does not, and
   on 2026-09-28 it released 8 leases that were all still being worked
   (SpatialEngine-u2x.30). The run reads the queue `bd` resolves from the
   current directory — the shared `.beads` database — so rehearse it with
   `--db <path>` against a scratch queue, never against the live one, and
   never from a test: `tools/test_no_real_queue.py` runs the whole tooling
   suite with `bd` and `paseo` shadowed and fails it if anything reaches for
   either. Any bead left `in_progress` for more than 90 minutes
   with no running paseo agent working its branch is stale: reclaim it, note
   why, and let it re-enter `bd ready`. Reclaim is for dead workers only —
   never a bead you stopped in TRIM, and never a bead labelled `human` waiting
   on a maintainer decision.

3. MERGE. For every bead labelled `needs-merge`, run
   `python3 tools/bd-merge-bead.py --bead <id>`. Do not do the steps by hand
   and do not call `bd close` yourself: the tool fetches, rebases the branch
   onto `origin/main`, runs the fast gate (`eng/verify.sh --fast`) on that
   rebased branch, merges to `main`, pushes, and closes only once
   `git merge-base --is-ancestor <merge> origin/main` passes. Both halves are
   the gate. The ancestor check is the publish half (SpatialEngine-xbz):
   closing on the strength of a green gate on the *branch* stranded three
   completed beads, and a merge that was never pushed made `origin/main` miss
   work that was already in `main`. The fast lane is the verify half
   (ADR-0134): the tool names `--fast` explicitly, never the bare
   `eng/verify.sh`, and it builds and tests what the branch changed rather than
   all fifty projects — a merge that must also run the exhaustive lane passes
   `--full`, which costs 15–25 minutes and is the right call for a change broad
   enough to doubt the scoping; `--skip-tests <substring>` leaves a named suite
   to CI on that merge, and prints and records what it dropped. The tool refuses
   rather than
   guesses — red verify, a rebase conflict, a failed push, or a `main` that is
   not published all leave the bead open with its work intact. If it exits
   non-zero, `bd note` the output on the bead and move on. Never close a bead
   whose gate is red. A red gate is re-run once on the same commit: green
   merges, red twice for the same reason is a stop condition.
   `python3 tools/bd-merge-bead.py --audit` reports closed beads whose
   recorded commit is not on `origin/main`; run it when a merge looks lost,
   before creating a recovery bead. It is a triage list, not proof of loss — a
   commit whose content was amended on the way in (an ADR renumbered) has a
   different patch-id and reads as stranded too. A re-run after a failed push
   passes `--verified`, which skips the gate because it was already green on
   that rebased commit; it skips nothing else, and the fact
   is written into the `bd close` reason.
   The merge commit the tool writes carries a `Task: <bead>` trailer, and every
   lane of `eng/verify.sh` reads it back (`tools/beads_gate.py`, ADR-0152): a
   closed bead's merge commit must name that bead, and a commit touching
   `src/Spatial.Contracts/**` or `src/Spatial.Core/**` must change an ADR or
   cite `ADR-NNNN` in its body. A hand-run `git merge --no-ff` on `main` is a
   red lane, which is the intent — the protocol above had already failed four
   ways in-tree (SpatialEngine-imz.4), and a rule only the merge tool follows
   is the rule the mis-dispatch got past.
   **Merges are local. There is no pull request.** The tool rebases the bead's
   branch onto `origin/main`, merges `--no-ff` into local `main`, pushes `main`
   and closes — that is the whole path. Do not open a GitHub pull request, do
   not wait on one, and do not treat a review as a gate. `git push` prints
   "Create a pull request for ... on GitHub"; that line is not an instruction.
   The consequence for the gates: `.github/workflows/ci.yml` also triggers on
   `pull_request`, and with no pull requests that job never fires, so the
   exhaustive lane arrives only on the post-merge push to `main` and the
   pre-merge signal is the fast lane alone (ADR-0134).
   A skip-heavy suite is not a green suite, and the gate now says so rather
   than leaving it to the person reading the log. `eng/verify.sh` used to exit 0
   while `Spatial.SqlServer.Tests` reported 10 passed / 104 skipped under
   parallel load — the very tests the bead existed to fix were among the skipped
   (SpatialEngine-u2x.58 lost a tick to exactly that). Every lane that runs
   `dotnet test` reads the per-suite skip counts back out of the trx files and
   fails a suite that skipped more than half of itself (`tools/skip_gate.py`,
   ADR-0139), so that run is now a red gate rather than a merge. A red gate on
   skips is not a broken build: re-run the affected class standalone on an idle
   box, or leave the suite to CI with `--skip-tests=<substring>` and say so in
   the close reason. `Spatial.Host.Tests` and `Spatial.PostGIS.Tests` running
   green is the check that Docker is reachable and the skips are contention, not
   a missing socket.
   Formatting is not a hand-off step and not a merge step (ADR-0134): CI and an
   occasional `--format` own it.

4. DRAIN — but only if the merge queue is empty. Count running workers with
   `paseo ls` — the agents this coordinator spawned, matched by the bead ids in
   their `in-flight` notes and by a worktree under `~/.paseo/worktrees/`; the
   coordinator runs in the main worktree and never counts. While workers < 3:
   - take from `bd ready`, in this order: children of `SpatialEngine-u2x` first,
     then **any other ready bead in the repo**. This second clause matters:
     workers are told to `bd create` a bead rather than widen their scope, and
     those follow-ups are the real work of the run. A filter of "epic children
     only" would strand them the moment the first wave lands.
   - skip anything labelled `in-flight`, `human`, or already claimed
   - read the epic's newest note block for the set currently blocked behind the
     DE-9IM/spatialRel reading (`SpatialEngine-onj`); that list has gone stale
     as beads closed under it, so re-check each entry rather than trusting the
     note, and do not treat the block as permanent
   - respect the file-overlap order in the epic's coordination note:
     **`.2` before `.16`, `.4` before `.21`**. If one of those two is claimed or
     in progress, skip its partner and pick another ready bead.
   - `bd update <id> --claim` (atomic; if it fails, someone else got it — move on)
   - `paseo workspace create --isolation worktree --mode branch-off
     --new-branch bd/<id> --base origin/main`
     If a worktree for that bead already exists from an earlier tick, reuse it
     (`paseo workspace ls`, match the title) instead of creating a second one —
     `branch-off` fails when the branch is already checked out.
   - `paseo run --provider pi/opencode-go/space-bunny-free --thinking medium
     -d --workspace <workspace-id> "<worker prompt below>"`
     `-d` is mandatory: without it `paseo run` blocks until the worker goes
     idle, so the spawns serialise and one tick never finishes.
   - label the bead `in-flight` and record the agent id + branch + workspace in
     its notes with `--append-notes`; a bare `--notes` has twice destroyed a
     bead's notes on this repo

   **BACKLOG GATE — a merge backlog blocks new spawns.** Re-read
   `bd list --label needs-merge` immediately before spawning. If any bead is
   still labelled `needs-merge` when DRAIN would run, spawn **nothing** this
   tick: report the unmerged ids and stop. Work already handed off outranks
   work not yet started, because a `needs-merge` bead is a worktree and an
   agent's worth of capacity sitting idle on a branch nobody is advancing, and
   because with 45 ready beads and 3 slots the queue refills the instant it
   drains — spawning first and merging later is how the backlog grows faster
   than it clears. The cap and the reclaim are the other reason: they are
   counted and cross-checked from one coordinator, and a second schedule doing
   the same arithmetic concurrently would double the effective cap and race
   `bd-safe-reclaim` on live leases. Merges stay in this one schedule.

5. REPORT. One short line per bead touched this tick, and the output of
   `python3 tools/bd-merge-bead.py --check` — a tick that did not finish
   `published: main is at origin/main` did something unrecoverable, and the
   report is where a human sees it. If nothing was ready and
   nothing is in flight, say "queue drained" and stop doing work. Do not poll,
   do not wait, do not sleep — the cron brings you back.

   The run ends when the queue is empty, not when the clock says so. Waves 2 to 4
   are inside the run, and so is any follow-up bead a worker created.

STOP CONDITIONS (any one, then do nothing more this tick):
- a bead fails its verify gate twice for the same reason
- a merge conflict that is not a trivial textual conflict
- anything that would need an architectural decision the bead did not already
  authorise (stop, and `bd create` a bead on the epic recording the decision
  needed)
- Docker has become unreachable again (`Spatial.Host.Tests` failing on
  Testcontainers is the tell) — merge nothing, spawn nothing, report it

## Worker prompt (verbatim, per bead)

**Substitution rule (added after the tick-3 mis-dispatch).** `BEAD_ID` is
substituted everywhere it appears. The header and the body must carry the *same*
id — the header used to be filled from the workspace name while the body was
filled from the bead the coordinator intended, and three workers each read the
body and did bead `.2` inside the `.1`, `.3` and `.4` worktrees. If you are
copying this prompt, replace every `BEAD_ID` in one pass and re-read the result
before you send it: no `BEAD_ID` may survive, and the id in the header and the
id in step 1 must be the same string. Never put a bare `<id>` in a spawned
prompt.

You are working bead `BEAD_ID`. You are in a dedicated git worktree checked out
on branch `bd/BEAD_ID`. The bead is already claimed by you.

1. `bd show BEAD_ID` and read the whole thing, plus the epic's coordination note.
   The description names the exact files and lines; trust them.
2. Read `AGENTS.md`, then the relevant `architecture/distilled/*.md` digest and
   the ADRs it routes to. The digests lose to the ADRs on conflict.
3. If the bead writes a decision record, **reserve its number first**:
   `python3 tools/adr-next-number.py --reserve --bead BEAD_ID` at the start of
   the branch, and `--check NNNN` again immediately before writing. The
   reservation is held in the repository's shared git dir, so a parallel branch
   that takes the same number is turned away at allocation rather than at merge
   (ADR-0090). Release it with `--release NNNN` if you renumber.
4. TEST FIRST. Write the failing reproduction test the acceptance criteria name
   (most beads name the exact wrong-behaviour case). Run it, watch it fail for
   the right reason. Only then fix.
5. Implement, staying inside the bead's scope. If you find a second problem, do
   not fix it — `bd create` a new bead with the evidence and carry on.
6. Lanes. `eng/verify.sh --fast` (the same as no arguments) is the **fast
   gate** — a build of the projects your change reaches and the test projects
   that reach it over `ProjectReference`, plus `Spatial.Architecture.Tests` —
   and it is what you run on every iteration *and* what the merge runs. It
   builds a scoped solution rather than all fifty projects and runs
   `dotnet test` once over it, which is most of why it is minutes rather than a
   quarter of an hour. Measured on this host: about a minute for a tooling or
   docs change, 2 m 40 s for a leaf `src/**` change, 7 m 32 s for a change
   under `Spatial.Core` — all green, nothing skipped.
   `eng/verify.sh --format` is the format lane (`dotnet format
   --verify-no-changes` over the changed
   projects, ~45 s each) and is **not** a hand-off step any more: formatting is
   enforced by CI and by an occasional run, not paid for hundreds of times a
   day (ADR-0134). `eng/verify.sh --full` is everything — format over the whole
   solution, build, every test project — for a change broad enough to doubt the
   scoping, and for CI, which runs it on the pull request and again after the
   merge to `main`. You do not run `--full` before a hand-off; the fast gate is
   your step, and nothing else (ADR-0118, as amended by ADR-0134). CI runs the
   exhaustive lane on the push to `main` that follows the merge; the swarm opens
   no pull requests, so it does not run on a pre-merge ref either.
   A change to a solution-wide file (a root `.props`,
   `Directory.Packages.props`, `.editorconfig`), or a change set the lane
   cannot read at all, makes every lane fall back to the whole solution rather
   than guess (ADR-0109). `eng/verify.sh --plan` prints what a lane would run.
   Every lane starts with `tools/trailing_whitespace.py` — a check, not a
   formatter, and there because `dotnet format` does not enforce the
   `trim_trailing_whitespace` the `.editorconfig` claims for `[*]` on a
   comment-only line (ADR-0143), and with `tools/conflict_markers.py`, which
   reads every tracked file for an unresolved merge-conflict marker (ADR-0146).
   Both are checks the lanes call directly rather than tests a tooling lane
   happens to discover — a rule that runs only when `tools/**` changed is not a
   gate, which is how a `<<<<<<< HEAD` line reached `CHANGELOG.md` on main
   through a docs merge the fast gate passed.
   `CI=true` with no lane named runs `--full`, so a workflow that calls the
   bare script gets the gate rather than the scoped lane.
7. A suite that fails for a reason unrelated to your change: prove it was
   already failing (name the bead and the failure) rather than editing the test
   to make it pass. A fast-gate run that skips a suite you expected is
   scoping working as designed, not a green light — CI on `main` covers what a
   merge did not run. Keep Docker reachable so the PostGIS and SQL Server
   integration suites actually run when the change reaches them. A suite that
   ran but *skipped most of its cases* is the other false green, and the gate
   fails it rather than trusting the exit code: under parallel worktrees
   `Spatial.SqlServer.Tests` has reported 10 passed / 104 skipped and exited 0
   (SpatialEngine-u2x.58), so a lane whose suite comes back skip-heavy is a red
   lane — run that class on its own before you hand off, or drop the suite with
   `--skip-tests=<substring>` and let CI on `main` have it.
8. Commit with a `Task: BEAD_ID` trailer, and **push your branch to
   origin** (`git push -u origin bd/BEAD_ID`). Then
   `bd update BEAD_ID --add-label needs-merge --append-notes "<commit> <branch>
   agent <id> workspace <id>"`. Do **not** close the bead — the coordinator
   merges, publishes and closes, in that order. Do **not** open a pull request:
   the coordinator merges locally with `tools/bd-merge-bead.py` and pushes
   `main`. The "Create a pull request for ... on GitHub" line a push prints is
   not an instruction.
9. Sanity check before you hand off: `git log -1 --format=%s%n%b` must name
   `Task: BEAD_ID`, and `bd show BEAD_ID` must show *your* bead as the one
   labelled `needs-merge`. If they disagree, you worked the wrong bead — say so
   in your summary rather than papering over it.

Rules that are not negotiable: no public contract change without an ADR first; no
third-party type crossing a contract; raw SQL is never reachable from a
client-supplied string; a parameter is either honoured or rejected by name, never
accepted and ignored.

<!-- orientation:begin -->

## Orientation

One line per closed bead: where the first hour went (`SpatialEngine-rzq`).

- Name the bead id in a worker prompt's first line and tell the worker to
  `bd show` it before writing code: a worker handed `.2` spent five hours
  building a duplicate of work already merged. (SpatialEngine-u2x.5)
- Hand off with `bd update <id> --add-label needs-merge --append-notes "…"`;
  `--label` and `--append-notes` with no `--notes` are the two spellings that
  error. (SpatialEngine-u2x.41)
- Read a lane that fails on `AdrNumberingTests` alone as inherited redness from
  `main`, not as the branch's own failure: a collision with a record merged
  after the branch-off only appears on rebase. (SpatialEngine-u2x.36)

<!-- orientation:end -->

## Notes

- `eng/verify.sh` (default, or `--fast`) is the fast gate, and it is the one that
  decides whether a merge is allowed — the merge tool runs it on the rebased
  branch before merging (ADR-0134). `eng/verify.sh --format` is the format
  lane, off the merge path; `eng/verify.sh --full` is the flat gate, opt-in per
  merge with `--bead --full` and run by CI on the push to `main` that follows
  every merge, once `main`'s CI is green and those jobs are required
  status checks (ADR-0118 §4; SpatialEngine-ivp, SpatialEngine-bv4). The swarm
  opens no pull requests, so ci.yml's `pull_request` trigger never fires and
  the pre-merge signal is the fast lane alone. At 3
  concurrent workers this host (12 cores / 15 GB) is already busy; if a tick
  finds the machine saturated, prefer merging (step 3) over spawning (step 4).
- The container-backed suites also run in their own CI job
  (`.github/workflows/ci.yml`, `integration`), on a runner that is not this
  box. A branch green there has already run the PostGIS, SQL Server, host and
  ingest suites; the local re-run before merge is still the coordinator's, and
  it is the one that wants an idle box to mean anything.
- The PostGIS and SQL Server integration suites start their own containers
  (ADR-0072, ADR-0073). The cap is 3 for that reason: at 8 this box was
  measured at load average 93–113, and container contention is what turns a
  green fast gate into a skip-laden one (`Spatial.SqlServer.Tests` reporting
  10 passed / 104 skipped under load, on the bead that existed to fix those
  tests). Drop the cap further if memory pressure shows up and say so in the
  tick report. The cap is a tuning knob, not a scope limit — the run still
  goes to exhaustion at a lower cap.
- The tier-1 chain is four serial steps (`.7` → `.8`/`.9` → `.11` → `.12`/`.13`).
  Those four are the long pole; everything else can proceed in parallel around
  them. If the chain stalls on `.7`, the rest of the queue still drains.
- No `paseo.json` exists in this repo, so there are no configured workspace
  scripts; the coordinator calls the CLI directly, as above.
