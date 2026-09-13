# Development task queue

A local, offline work queue for agents and humans changing Spatial Engine.
It is **development infrastructure**: no ADR, no `src/` code, no product
surface. `tools/tasks/tasks.mjs` is the whole implementation, with a thin
`eng/tasks` wrapper.

Drive it from the repository root:

```bash
eng/tasks add "Widen WMS GetFeatureInfo tolerance" --area interop.esri --priority 2
eng/tasks list
eng/tasks next --claim --json
eng/tasks submit T-012 --commit <sha> --pr 34
eng/tasks done T-012
```

## Where the data lives

The database is resolved in this order:

1. `--db PATH`
2. `$SPATIAL_TASKS_DB`
3. `<git-common-dir>/spatial-tasks/tasks.db` — **shared by every git worktree**
4. `$XDG_STATE_HOME/spatial-engine/tasks.db` (fallback outside a repo)

`eng/tasks where` prints the resolved path. The database is deliberately
**not** committed: it is local coordination state, not history. Provenance
lives in git via the `Task: <id>` commit trailer.

## Worktrees and parallel agents

The DB sits on the shared git dir, so a worktree created with
`git worktree add` or `paseo workspace create --isolation worktree` sees the
same queue. SQLite runs in WAL mode with a 5 s busy timeout, and every claim
is a single `BEGIN IMMEDIATE` transaction — concurrent `next --claim` calls
serialise, and exactly one agent wins a task. (Loosen nothing here: the
atomic claim is the whole point. It is only safe on a local filesystem, not
NFS/SMB. If agents run in containers, mount one shared volume and verify
locking, or move to a server.)

`claim` records the lease, `worktree` and `branch` on the row, so a crashed
agent is recoverable: after the lease expires, `eng/tasks reclaim` returns
the task to `ready`.

## Lifecycle

```
ready ──claim──► claimed ──submit──► review ──done──► done
                   │                              (eng/verify.sh green on main)
                   └──stale lease──► ready
```

- **ready** — actionable: no unmet dependencies, nobody owns it.
- **claimed** — owned by one agent until the lease expires.
- **review** — the branch/PR exists; waiting on serialised integration.
  Not leased.
- **done** — merged to `main` and verified there, never on the branch.
- plus `blocked`, `failed`, `cancelled`.

A dependency is met when its task is `done` or `cancelled`. `add`/`edit`
refuse to create dependency cycles.

## Commands

| Command | Purpose |
| --- | --- |
| `add <title>` | Capture. `--body -` reads stdin; `--area`, `--priority 1..5`, `--dep T-N`, `--id`. |
| `edit <id>` | Change title/body/area/priority; `--dep-add`, `--dep-remove`. |
| `list` | Open tasks by default; `--status open\|all\|<status>`, `--area`, `--agent`, `--limit`. |
| `ready` | Actionable tasks only (status `ready` **and** unblocked). |
| `next [--claim]` | Top of the ready queue; `--claim` takes it atomically. |
| `show <id>` | Task, body, dependencies, full history. |
| `claim <id>` | Claim a specific task; `--agent`, `--lease MIN`, `--worktree`, `--branch`. |
| `submit <id>` | `claimed -> review`; `--commit`, `--pr`, `--note`. |
| `done <id>` | `-> done`; `--commit`, `--note`, `--verify` runs `eng/verify.sh` first. |
| `block <id> --on X` | Block on a task id or free text (`--reason`). `unblock <id>` clears it. |
| `fail` / `cancel` / `reopen` | Terminal and recovery transitions. |
| `reclaim` | Expired leases back to `ready` (`--dry-run`). |
| `events [id]` | Audit trail. |
| `stats` | Counts, actionable vs dependency-waiting, expired leases. |
| `export` | Markdown snapshot of open tasks. |

### Output and exit codes

`--json` everywhere (the default when stdout is not a TTY, so agents get
machine output automatically); `--md` or `--table` for humans. Exit codes:
`0` ok, `1` error, `2` no ready task, `3` claim conflict.

## Agent protocol

1. `eng/tasks next --claim --agent <id> --json` — never start unclaimed work.
2. Work in the task's worktree; run `eng/verify.sh` before handing off.
3. `eng/tasks submit <id> --commit <sha> --pr <n>`.
4. The merger lands it on `main`, runs `eng/verify.sh` there, then
   `eng/tasks done <id>`.

Record the task in the commit trailer so provenance survives without the DB:

```
Task: T-012
```

## Concurrency notes

- `Directory.Build.props`/`Directory.Packages.props`/`SpatialEngine.slnx` and
  `CHANGELOG.md` are merge hot spots. The merger owns them, or the agent
  rebases onto `main` before `submit`.
- Allocate an ADR number **at claim time** (copy it into the task body) so
  parallel tasks do not both pick `architecture/decisions/ADR-0054-…`.
