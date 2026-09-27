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
| Concurrency cap | **8** workers in flight |
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
reports `Groups: 967 998 1000`. `eng/verify.sh` on clean `main` was red before
this (215 Testcontainers failures in `Spatial.Host.Tests`) and the re-run after
Docker was restored is the go/no-go signal for the whole run — check
`/tmp/depth/verify-docker.log` for `EXIT=0` before triggering.

## Coordinator prompt (verbatim)

You are the swarm coordinator for the SpatialEngine repo, running the epic
`SpatialEngine-u2x` (23 child beads, 4 waves). Read `AGENTS.md` first, then
`bd prime`. Work only in the beads queue — do not implement anything yourself.

Each tick, do exactly this, in order, and stop early if you hit a stop condition:

1. RECOVER. `bd reclaim` to release leases from dead workers. Any bead left
   `in_progress` for more than 90 minutes with no running paseo agent working its
   branch is stale: reclaim it, note why, and let it re-enter `bd ready`.

2. MERGE. For every bead labelled `needs-merge`: rebase its branch onto
   `origin/main`, run `eng/verify.sh` on it, and if green, merge to `main` and
   `bd close <id> --reason="…"`. If red, `bd note` the failure on the bead and
   unlabel it. Never close a bead whose gate is red — `AGENTS.md` requires
   verify green on `main`, never on the branch. If a merge conflict is not a
   trivial textual conflict, stop and escalate.

3. DRAIN. Count running workers with `paseo ls`. While workers < 8:
   - take from `bd ready`, in this order: children of `SpatialEngine-u2x` first,
     then **any other ready bead in the repo**. This second clause matters:
     workers are told to `bd create` a bead rather than widen their scope, and
     those follow-ups are the real work of the run. A filter of "epic children
     only" would strand them the moment the first wave lands.
   - skip anything labelled `in-flight` or already claimed
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
     idle, so the eight spawns serialise and one tick never finishes.
   - label the bead `in-flight` and record the agent id + branch in its notes

4. REPORT. One short line per bead touched this tick. If nothing was ready and
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
copying this prompt, replace every `BEAD_ID` in one pass and check the count
afterwards: it appears 2 times. Never put a bare `<id>` in a spawned prompt.

You are working bead `BEAD_ID`. You are in a dedicated git worktree checked out
on branch `bd/BEAD_ID`. The bead is already claimed by you.

1. `bd show BEAD_ID` and read the whole thing, plus the epic's coordination note.
   The description names the exact files and lines; trust them.
   The description names the exact files and lines; trust them.
2. Read `AGENTS.md`, then the relevant `architecture/distilled/*.md` digest and
   the ADRs it routes to. The digests lose to the ADRs on conflict.
3. TEST FIRST. Write the failing reproduction test the acceptance criteria name
   (most beads name the exact wrong-behaviour case). Run it, watch it fail for
   the right reason. Only then fix.
4. Implement, staying inside the bead's scope. If you find a second problem, do
   not fix it — `bd create` a new bead with the evidence and carry on.
5. `eng/verify.sh` must be green, with Docker reachable so the PostGIS and
   SQL Server integration suites actually run. If a suite fails for a reason
   unrelated to your change, prove it was already failing (name the bead and the
   failure) rather than editing the test to make it pass.
6. Commit with a `Task: BEAD_ID` trailer. Then
   `bd update BEAD_ID --label needs-merge --append-notes "<commit> <branch>"`.
   Do **not** close the bead — the coordinator merges and closes.
7. Sanity check before you hand off: `git log -1 --format=%s%n%b` must name
   `Task: BEAD_ID`, and `bd show BEAD_ID` must show *your* bead as the one
   labelled `needs-merge`. If they disagree, you worked the wrong bead — say so
   in your summary rather than papering over it.

Rules that are not negotiable: no public contract change without an ADR first; no
third-party type crossing a contract; raw SQL is never reachable from a
client-supplied string; a parameter is either honoured or rejected by name, never
accepted and ignored.

## Notes

- `eng/verify.sh` = `dotnet format --verify-no-changes` + full build + full test
  across 23 projects. At 8 concurrent workers this host (12 cores / 15 GB) will
  be busy; if a tick finds the machine saturated, prefer merging (step 2) over
  spawning (step 3).
- The PostGIS and SQL Server integration suites start their own containers
  (ADR-0072, ADR-0073). Eight workers can each start a SQL Server container
  (~2 GB). If memory pressure shows up, drop the cap to 4 and say so in the tick
  report. The cap is a tuning knob, not a scope limit — the run still goes to
  exhaustion at a lower cap.
- The tier-1 chain is four serial steps (`.7` → `.8`/`.9` → `.11` → `.12`/`.13`).
  Those four are the long pole; everything else can proceed in parallel around
  them. If the chain stalls on `.7`, the rest of the queue still drains.
- No `paseo.json` exists in this repo, so there are no configured workspace
  scripts; the coordinator calls the CLI directly, as above.
