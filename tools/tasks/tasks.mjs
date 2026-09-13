#!/usr/bin/env node
// Spatial Engine development task queue.
//
// A local, offline, git-worktree-aware work queue. The database lives on the
// *shared* git dir (`<git-common-dir>/spatial-tasks/tasks.db`) so every worktree
// of this repository sees the same queue; the CLI itself is committed and each
// worktree runs its own copy.
//
// This is development infrastructure, not product. It has no ADR and no place
// in `src/`. It coordinates the agents and humans that change the product.
//
// Lifecycle:
//
//   ready ──claim──► claimed ──submit──► review ──done──► done
//                      │                              (green on main)
//                      └──stale lease──► ready
//
// Exit codes: 0 ok · 1 error · 2 no ready task · 3 claim conflict.

import { DatabaseSync } from 'node:sqlite';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const STATUSES = ['ready', 'claimed', 'review', 'done', 'blocked', 'failed', 'cancelled'];

// Agents pipe output to `head`/`jq`; do not crash with EPIPE when the reader
// closes early.
process.stdout.on('error', (err) => {
  if (err && err.code === 'EPIPE') process.exit(0);
  throw err;
});
const OPEN_STATUSES = ['ready', 'claimed', 'review', 'blocked'];
const SATISFIED = "('done','cancelled')"; // a dependency in one of these is met
const DEFAULT_LEASE_MIN = 45;
const DEFAULT_PRIORITY = 3;
const PROG = 'tasks';

const SCHEMA = `
CREATE TABLE IF NOT EXISTS meta (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS tasks (
  id          TEXT PRIMARY KEY,
  seq         INTEGER NOT NULL UNIQUE,
  title       TEXT NOT NULL,
  body        TEXT NOT NULL DEFAULT '',
  area        TEXT NOT NULL DEFAULT '',
  priority    INTEGER NOT NULL DEFAULT 3,
  status      TEXT NOT NULL DEFAULT 'ready',
  agent       TEXT,
  lease_until TEXT,
  worktree    TEXT,
  branch      TEXT,
  commit_sha  TEXT,
  pr          TEXT,
  blocked_on  TEXT,
  note        TEXT,
  created     TEXT NOT NULL,
  updated     TEXT NOT NULL,
  claimed_at  TEXT,
  finished_at TEXT
);
CREATE INDEX IF NOT EXISTS idx_tasks_status ON tasks(status, priority, seq);
CREATE TABLE IF NOT EXISTS deps (
  task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
  dep_id  TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
  PRIMARY KEY (task_id, dep_id)
);
CREATE INDEX IF NOT EXISTS idx_deps_dep ON deps(dep_id);
CREATE TABLE IF NOT EXISTS events (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  task_id     TEXT NOT NULL,
  at          TEXT NOT NULL,
  actor       TEXT,
  from_status TEXT,
  to_status   TEXT,
  detail      TEXT
);
CREATE INDEX IF NOT EXISTS idx_events_task ON events(task_id, id);
`;

// ---------------------------------------------------------------------------
// small helpers

const die = (msg, code = 1) => {
  process.stderr.write(`${PROG}: ${msg}\n`);
  process.exit(code);
};

const nowIso = () => new Date().toISOString();
const leaseIso = (min) => new Date(Date.now() + min * 60_000).toISOString();
const uniq = (xs) => [...new Set(xs)];
const plain = (row) => (row ? Object.assign({}, row) : row);
const plainAll = (rows) => rows.map((r) => Object.assign({}, r));

function git(args) {
  return execFileSync('git', args, {
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'ignore'],
  }).trim();
}

function repoCommonDir() {
  try {
    const out = git(['rev-parse', '--path-format=absolute', '--git-common-dir']);
    return path.isAbsolute(out) ? out : path.resolve(process.cwd(), out);
  } catch {
    return null;
  }
}

function resolveDbPath(explicit) {
  if (explicit) return path.resolve(explicit);
  if (process.env.SPATIAL_TASKS_DB) return path.resolve(process.env.SPATIAL_TASKS_DB);
  const common = repoCommonDir();
  if (common) return path.join(common, 'spatial-tasks', 'tasks.db');
  const state = process.env.XDG_STATE_HOME || path.join(os.homedir(), '.local', 'state');
  return path.join(state, 'spatial-engine', 'tasks.db');
}

function resolveActor(explicit) {
  return (
    explicit ||
    process.env.SPATIAL_TASK_AGENT ||
    process.env.PASEO_AGENT_ID ||
    process.env.PASEO_AGENT_NAME ||
    process.env.USER ||
    'unknown'
  );
}

function openDb(dbPath) {
  fs.mkdirSync(path.dirname(dbPath), { recursive: true });
  const db = new DatabaseSync(dbPath);
  db.exec('PRAGMA journal_mode=WAL;');
  db.exec('PRAGMA busy_timeout=5000;');
  db.exec('PRAGMA foreign_keys=ON;');
  db.exec(SCHEMA);
  db.prepare("INSERT OR IGNORE INTO meta(key,value) VALUES('schema_version','1')").run();
  return db;
}

function withTx(db, fn) {
  db.exec('BEGIN IMMEDIATE');
  try {
    const out = fn();
    db.exec('COMMIT');
    return out;
  } catch (err) {
    try {
      db.exec('ROLLBACK');
    } catch {
      /* ignore */
    }
    throw err;
  }
}

function getTask(db, id) {
  return plain(db.prepare('SELECT * FROM tasks WHERE id = ?').get(id));
}

function mustTask(db, id) {
  const t = getTask(db, id);
  if (!t) die(`no such task: ${id}`);
  return t;
}

function depsOf(db, id) {
  return db
    .prepare(
      `SELECT d.dep_id AS id, p.title, p.status
         FROM deps d LEFT JOIN tasks p ON p.id = d.dep_id
        WHERE d.task_id = ? ORDER BY d.dep_id`
    )
    .all(id)
    .map((r) => Object.assign({}, r));
}

function unmetDeps(db, id) {
  return db
    .prepare(
      `SELECT d.dep_id AS id, p.status
         FROM deps d JOIN tasks p ON p.id = d.dep_id
        WHERE d.task_id = ? AND p.status NOT IN ${SATISFIED}
        ORDER BY d.dep_id`
    )
    .all(id)
    .map((r) => Object.assign({}, r));
}

function isReady(db, id) {
  const t = getTask(db, id);
  return !!t && t.status === 'ready' && unmetDeps(db, id).length === 0;
}

const READY_CLAUSE = `t.status = 'ready' AND NOT EXISTS (
  SELECT 1 FROM deps d JOIN tasks p ON p.id = d.dep_id
   WHERE d.task_id = t.id AND p.status NOT IN ${SATISFIED})`;

function readyTasks(db, { area, limit } = {}) {
  const where = [READY_CLAUSE];
  const params = [];
  if (area) {
    where.push('t.area = ?');
    params.push(area);
  }
  let sql = `SELECT t.* FROM tasks t WHERE ${where.join(' AND ')} ORDER BY t.priority, t.seq`;
  if (limit) {
    sql += ' LIMIT ?';
    params.push(limit);
  }
  return plainAll(db.prepare(sql).all(...params));
}

function recordEvent(db, taskId, { actor, from, to, detail }) {
  db.prepare(
    `INSERT INTO events(task_id, at, actor, from_status, to_status, detail)
     VALUES(?,?,?,?,?,?)`
  ).run(taskId, nowIso(), actor ?? null, from ?? null, to ?? null, detail ?? null);
}

function nextSeq(db) {
  return db.prepare('SELECT COALESCE(MAX(seq),0)+1 AS n FROM tasks').get().n;
}

function hasCycle(db, taskId, depId) {
  // Would adding task -> dep create a cycle? Walk dep's dependency closure.
  if (taskId === depId) return true;
  const seen = new Set();
  const stack = [depId];
  const stmt = db.prepare('SELECT dep_id FROM deps WHERE task_id = ?');
  while (stack.length) {
    const cur = stack.pop();
    if (cur === taskId) return true;
    if (seen.has(cur)) continue;
    seen.add(cur);
    for (const r of stmt.all(cur)) stack.push(r.dep_id);
  }
  return false;
}

// ---------------------------------------------------------------------------
// argument parsing

const BOOL_FLAGS = new Set([
  'json',
  'md',
  'table',
  'quiet',
  'claim',
  'dry-run',
  'verify',
  'help',
  'version',
  'all',
]);

function parseArgs(argv) {
  const flags = new Map();
  const bools = new Set();
  const pos = [];
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--') {
      pos.push(...argv.slice(i + 1));
      break;
    }
    if (!a.startsWith('--')) {
      pos.push(a);
      continue;
    }
    const eq = a.indexOf('=');
    const key = eq >= 0 ? a.slice(2, eq) : a.slice(2);
    if (BOOL_FLAGS.has(key)) {
      bools.add(key);
      continue;
    }
    let val = eq >= 0 ? a.slice(eq + 1) : undefined;
    if (val === undefined) {
      val = argv[++i];
      if (val === undefined) die(`--${key} needs a value`);
    }
    if (!flags.has(key)) flags.set(key, []);
    flags.get(key).push(val);
  }
  return {
    flags,
    bools,
    pos,
    one: (k) => (flags.has(k) ? flags.get(k).at(-1) : undefined),
    many: (k) => flags.get(k) ?? [],
    has: (k) => flags.has(k) || bools.has(k),
  };
}

function readBody(inline) {
  if (inline === undefined) return '';
  if (inline !== '-') return inline;
  try {
    return fs.readFileSync(0, 'utf8');
  } catch {
    return '';
  }
}

// Validate an integer CLI argument up front and fail with a clean `tasks:`
// diagnostic, rather than letting the value reach SQLite (datatype mismatch) or
// `Date` (RangeError) and surface as an uncaught Node stack trace.
function intArg(value, name, { min, max } = {}) {
  const text = String(value);
  if (!/^-?\d+$/.test(text)) die(`--${name} must be an integer, got '${text}'`);
  const n = Number(text);
  if ((min !== undefined && n < min) || (max !== undefined && n > max)) {
    const range =
      min !== undefined && max !== undefined
        ? `${min}..${max}`
        : min !== undefined
          ? `>= ${min}`
          : `<= ${max}`;
    die(`--${name} must be ${range}, got '${text}'`);
  }
  return n;
}

// ---------------------------------------------------------------------------
// rendering

function wantsJson(args) {
  return args.bools.has('json') || (!process.stdout.isTTY && !args.bools.has('md') && !args.bools.has('table'));
}

function emitJson(value) {
  process.stdout.write(JSON.stringify(value, null, 2) + '\n');
}

function pad(s, n) {
  s = String(s ?? '');
  return s.length >= n ? s : s + ' '.repeat(n - s.length);
}

function renderTable(rows, cols, widths) {
  const head = cols.map((c, i) => pad(c.label, widths[i])).join('  ');
  const lines = [head, widths.map((w) => '-'.repeat(w)).join('  ')];
  for (const r of rows) {
    lines.push(cols.map((c, i) => pad(c.get(r), widths[i])).join('  '));
  }
  return lines.join('\n');
}

function shortLease(t) {
  if (t.status !== 'claimed' || !t.lease_until) return '';
  const mins = Math.round((Date.parse(t.lease_until) - Date.now()) / 60_000);
  return mins < 0 ? `expired ${-mins}m` : `${mins}m`;
}

function renderTaskList(tasks, { md = false } = {}) {
  if (!tasks.length) return '(no tasks)';
  const cols = [
    { label: 'ID', get: (t) => t.id },
    { label: 'P', get: (t) => t.priority },
    { label: 'STATUS', get: (t) => t.status },
    { label: 'AREA', get: (t) => t.area || '-' },
    { label: 'AGENT', get: (t) => t.agent || '-' },
    { label: 'LEASE', get: shortLease },
    { label: 'TITLE', get: (t) => t.title },
  ];
  if (md) {
    const esc = (s) => String(s ?? '').replace(/\|/g, '\\|');
    const head = `| ${cols.map((c) => c.label).join(' | ')} |`;
    const rule = `| ${cols.map((c) => (c.label === 'P' || c.label === 'LEASE' ? '---:' : '---')).join(' | ')} |`;
    const body = tasks.map((t) => `| ${cols.map((c) => esc(c.get(t))).join(' | ')} |`);
    return [head, rule, ...body].join('\n');
  }
  const widths = cols.map((c) => Math.max(c.label.length, ...tasks.map((t) => String(c.get(t) ?? '').length)));
  return renderTable(tasks, cols, widths);
}

function renderTask(t) {
  const out = [
    `${t.id}  [${t.status}]  ${t.title}`,
    `  priority : ${t.priority}`,
    `  area     : ${t.area || '-'}`,
    `  created  : ${t.created}`,
    `  updated  : ${t.updated}`,
  ];
  if (t.agent) out.push(`  agent    : ${t.agent}`);
  if (t.lease_until) out.push(`  lease    : ${t.lease_until} (${shortLease(t)})`);
  if (t.worktree) out.push(`  worktree : ${t.worktree}`);
  if (t.branch) out.push(`  branch   : ${t.branch}`);
  if (t.commit_sha) out.push(`  commit   : ${t.commit_sha}`);
  if (t.pr) out.push(`  pr       : ${t.pr}`);
  if (t.blocked_on) out.push(`  blocked  : ${t.blocked_on}`);
  if (t.note) out.push(`  note     : ${t.note}`);
  return out.join('\n');
}

// ---------------------------------------------------------------------------
// commands

const HELP = `Spatial Engine task queue.

Usage: tasks [--db PATH] [--json|--md] <command> [args]

Capture
  add <title>            Add a task. --body - reads stdin; --area A; --priority N;
                         --dep T-N (repeatable); --id T-N.
  edit <id>              Change a task. --body - reads stdin; --title/--area/
                         --priority/--dep-add/--dep-remove.

Pick
  list                   Open tasks (--status ready|claimed|review|done|blocked|
                         failed|cancelled|all, --area A, --agent A, --limit N).
  ready                  Unblocked, unclaimed tasks (--area, --limit).
  next [--claim]         Show the top ready task; with --claim, claim it atomically
                         (--agent A --lease MIN --worktree P --branch B).
  show <id>              Full task, its dependencies and history.

Progress
  claim <id>             Claim a specific ready task. --agent A --lease MIN
                         --worktree P --branch B.
  submit <id>            Branch/PR ready for integration: status -> review.
                         --commit SHA --pr N --note TEXT.
  done <id>              Mark complete. --commit SHA --note TEXT; --verify runs
                         eng/verify.sh first and only commits on green.
  block <id> --on X      Block (--on is a task id or free text). --reason TEXT.
  unblock <id>           Blocked -> ready.
  fail <id> / cancel <id> / reopen <id>

Maintain
  reclaim                Return lease-expired claims to ready (--dry-run).
  events [id]            History (--limit N).
  stats                  Status counts and queue health.
  export                 Markdown snapshot of open tasks.
  where                  Print the resolved database path.

Exit codes: 0 ok · 1 error · 2 no ready task · 3 claim conflict.`;

function cmdAdd(db, args) {
  const title = (args.one('title') ?? args.pos.join(' ')).trim();
  if (!title) die('add needs a title');
  const area = args.one('area') ?? '';
  const priority =
    args.one('priority') === undefined
      ? DEFAULT_PRIORITY
      : intArg(args.one('priority'), 'priority', { min: 1, max: 5 });
  const body = readBody(args.one('body'));
  const actor = resolveActor(args.one('agent'));
  const deps = uniq(args.many('dep'));

  const created = withTx(db, () => {
    const seq = nextSeq(db);
    const id = args.one('id') ?? `T-${String(seq).padStart(3, '0')}`;
    if (getTask(db, id)) die(`task already exists: ${id}`);
    for (const d of deps) {
      if (!getTask(db, d)) die(`--dep ${d} does not exist`);
      if (hasCycle(db, id, d)) die(`--dep ${d} would create a dependency cycle`);
    }
    const at = nowIso();
    db.prepare(
      `INSERT INTO tasks(id,seq,title,body,area,priority,status,created,updated)
       VALUES(?,?,?,?,?,?, 'ready', ?, ?)`
    ).run(id, seq, title, body, area, priority, at, at);
    const ins = db.prepare('INSERT INTO deps(task_id,dep_id) VALUES(?,?)');
    for (const d of deps) ins.run(id, d);
    recordEvent(db, id, { actor, to: 'ready', detail: `created${deps.length ? ` deps=${deps.join(',')}` : ''}` });
    return id;
  });

  const t = getTask(db, created);
  if (wantsJson(args)) emitJson({ task: t, deps: depsOf(db, created) });
  else process.stdout.write(`added ${t.id}  ${t.title}\n`);
}

function cmdEdit(db, args) {
  const id = args.pos[0];
  if (!id) die('edit needs a task id');
  const t = mustTask(db, id);
  const actor = resolveActor(args.one('agent'));
  const add = uniq(args.many('dep-add'));
  const remove = uniq(args.many('dep-remove'));

  withTx(db, () => {
    const sets = [];
    const params = [];
    for (const [flag, col] of [['title', 'title'], ['body', 'body'], ['area', 'area']]) {
      if (args.has(flag)) {
        sets.push(`${col} = ?`);
        params.push(flag === 'body' ? readBody(args.one(flag)) : args.one(flag));
      }
    }
    if (args.has('priority')) {
      sets.push('priority = ?');
      params.push(intArg(args.one('priority'), 'priority', { min: 1, max: 5 }));
    }
    for (const d of add) {
      if (!getTask(db, d)) die(`--dep-add ${d} does not exist`);
      if (hasCycle(db, id, d)) die(`--dep-add ${d} would create a dependency cycle`);
    }
    sets.push('updated = ?');
    params.push(nowIso(), id);
    db.prepare(`UPDATE tasks SET ${sets.join(', ')} WHERE id = ?`).run(...params);
    for (const d of add) db.prepare('INSERT OR IGNORE INTO deps(task_id,dep_id) VALUES(?,?)').run(id, d);
    for (const d of remove) db.prepare('DELETE FROM deps WHERE task_id=? AND dep_id=?').run(id, d);
    recordEvent(db, id, { actor, from: t.status, to: t.status, detail: 'edited' });
  });

  const out = getTask(db, id);
  if (wantsJson(args)) emitJson({ task: out, deps: depsOf(db, id) });
  else process.stdout.write(renderTask(out) + '\n');
}

function cmdList(db, args) {
  const status = args.one('status') ?? (args.bools.has('all') ? 'all' : 'open');
  if (status !== 'all' && status !== 'open' && !STATUSES.includes(status)) {
    die(`--status must be open, all, or one of: ${STATUSES.join(', ')}`);
  }
  const where = [];
  const params = [];
  if (status === 'open') where.push(`status IN (${OPEN_STATUSES.map(() => '?').join(',')})`), params.push(...OPEN_STATUSES);
  else if (status !== 'all') where.push('status = ?'), params.push(status);
  if (args.one('area')) where.push('area = ?'), params.push(args.one('area'));
  if (args.one('agent')) where.push('agent = ?'), params.push(args.one('agent'));
  let sql = `SELECT * FROM tasks ${where.length ? 'WHERE ' + where.join(' AND ') : ''} ORDER BY priority, seq`;
  if (args.one('limit') !== undefined) {
    sql += ' LIMIT ?';
    params.push(intArg(args.one('limit'), 'limit', { min: 1 }));
  }
  const rows = plainAll(db.prepare(sql).all(...params));
  if (wantsJson(args)) emitJson(rows);
  else process.stdout.write(renderTaskList(rows, { md: args.bools.has('md') }) + '\n');
}

function cmdReady(db, args) {
  const limit = args.one('limit') === undefined ? undefined : intArg(args.one('limit'), 'limit', { min: 1 });
  const rows = readyTasks(db, { area: args.one('area'), limit });
  if (wantsJson(args)) emitJson(rows);
  else process.stdout.write(renderTaskList(rows, { md: args.bools.has('md') }) + '\n');
}

function claimTask(db, id, { agent, leaseMin, worktree, branch, actor }) {
  return withTx(db, () => {
    const t = getTask(db, id);
    if (!t) return { ok: false, reason: `no such task: ${id}` };
    const expired = t.status === 'claimed' && t.lease_until && t.lease_until < nowIso();
    if (t.status !== 'ready' && !expired) return { ok: false, reason: `${id} is ${t.status}` };
    const unmet = unmetDeps(db, id);
    if (unmet.length) return { ok: false, reason: `${id} is blocked on ${unmet.map((d) => d.id).join(', ')}` };
    const at = nowIso();
    db.prepare(
      `UPDATE tasks SET status='claimed', agent=?, lease_until=?, worktree=?, branch=?,
                        claimed_at=?, updated=? WHERE id=?`
    ).run(agent, leaseIso(leaseMin), worktree ?? null, branch ?? null, at, at, id);
    recordEvent(db, id, {
      actor,
      from: t.status,
      to: 'claimed',
      detail: [`lease=${leaseMin}m`, worktree ? `worktree=${worktree}` : null, branch ? `branch=${branch}` : null]
        .filter(Boolean)
        .join(' '),
    });
    return { ok: true, task: getTask(db, id) };
  });
}

function cmdClaim(db, args) {
  const id = args.pos[0];
  if (!id) die('claim needs a task id');
  const agent = resolveActor(args.one('agent'));
  const leaseMin =
    args.one('lease') === undefined ? DEFAULT_LEASE_MIN : intArg(args.one('lease'), 'lease', { min: 1 });
  const res = claimTask(db, id, {
    agent,
    leaseMin,
    worktree: args.one('worktree'),
    branch: args.one('branch'),
    actor: agent,
  });
  if (!res.ok) die(res.reason, 3);
  if (wantsJson(args)) emitJson({ task: res.task, deps: depsOf(db, id) });
  else process.stdout.write(`claimed ${res.task.id} as ${agent} (lease ${leaseMin}m)\n`);
}

function cmdNext(db, args) {
  if (!args.bools.has('claim')) {
    const [t] = readyTasks(db, { area: args.one('area'), limit: 1 });
    if (!t) die('no ready task', 2);
    if (wantsJson(args)) emitJson({ task: t, deps: depsOf(db, t.id) });
    else process.stdout.write(renderTask(t) + '\n');
    return;
  }
  const agent = resolveActor(args.one('agent'));
  const leaseMin =
    args.one('lease') === undefined ? DEFAULT_LEASE_MIN : intArg(args.one('lease'), 'lease', { min: 1 });
  const worktree = args.one('worktree');
  const branch = args.one('branch');
  const res = withTx(db, () => {
    const where = [READY_CLAUSE];
    const params = [];
    if (args.one('area')) where.push('t.area = ?'), params.push(args.one('area'));
    const t = db
      .prepare(`SELECT t.* FROM tasks t WHERE ${where.join(' AND ')} ORDER BY t.priority, t.seq LIMIT 1`)
      .get(...params);
    if (!t) return { ok: false };
    const id = t.id;
    const at = nowIso();
    db.prepare(
      `UPDATE tasks SET status='claimed', agent=?, lease_until=?, worktree=?, branch=?, claimed_at=?, updated=?
       WHERE id=? AND status='ready'`
    ).run(agent, leaseIso(leaseMin), worktree ?? null, branch ?? null, at, at, id);
    recordEvent(db, id, { actor: agent, from: 'ready', to: 'claimed', detail: `lease=${leaseMin}m via next` });
    return { ok: true, id };
  });
  if (!res.ok) die('no ready task', 2);
  const t = getTask(db, res.id);
  if (wantsJson(args)) emitJson({ task: t, deps: depsOf(db, t.id) });
  else process.stdout.write(`claimed ${t.id} as ${agent} (lease ${leaseMin}m)\n${renderTask(t)}\n`);
}

function cmdSubmit(db, args) {
  const id = args.pos[0];
  if (!id) die('submit needs a task id');
  const t = mustTask(db, id);
  if (!['claimed', 'review'].includes(t.status)) die(`${id} is ${t.status}, not claimed/review`, 3);
  const actor = resolveActor(args.one('agent'));
  const commit = args.one('commit');
  if (commit && repoCommonDir()) {
    try {
      execFileSync('git', ['cat-file', '-e', `${commit}^{commit}`], { stdio: ['ignore', 'ignore', 'ignore'] });
    } catch {
      die(`--commit ${commit} is not a commit in this repository`);
    }
  }
  withTx(db, () => {
    db.prepare(
      `UPDATE tasks SET status='review', commit_sha=COALESCE(?,commit_sha), pr=COALESCE(?,pr),
                        note=COALESCE(?,note), updated=? WHERE id=?`
    ).run(commit ?? null, args.one('pr') ?? null, args.one('note') ?? null, nowIso(), id);
    recordEvent(db, id, {
      actor,
      from: t.status,
      to: 'review',
      detail: [`commit=${commit ?? '-'}`, args.one('pr') ? `pr=${args.one('pr')}` : null].filter(Boolean).join(' '),
    });
  });
  const out = getTask(db, id);
  if (wantsJson(args)) emitJson({ task: out });
  else process.stdout.write(`${out.id} -> review${commit ? ` @ ${commit}` : ''}\n`);
}

function cmdDone(db, args) {
  const id = args.pos[0];
  if (!id) die('done needs a task id');
  const t = mustTask(db, id);
  if (!['ready', 'claimed', 'review'].includes(t.status)) die(`${id} is ${t.status}`, 3);
  const actor = resolveActor(args.one('agent'));
  const commit = args.one('commit') ?? t.commit_sha;

  if (args.bools.has('verify')) {
    let root;
    try {
      root = git(['rev-parse', '--show-toplevel']);
    } catch {
      die('--verify needs a git worktree');
    }
    process.stderr.write(`${PROG}: running eng/verify.sh...\n`);
    try {
      execFileSync('bash', [path.join(root, 'eng', 'verify.sh')], { stdio: 'inherit' });
    } catch {
      die('eng/verify.sh failed; task left at its current status');
    }
  }

  withTx(db, () => {
    const at = nowIso();
    db.prepare(
      `UPDATE tasks SET status='done', commit_sha=COALESCE(?,commit_sha), note=COALESCE(?,note),
                        finished_at=?, updated=?, agent=COALESCE(agent,?) WHERE id=?`
    ).run(commit ?? null, args.one('note') ?? null, at, at, actor, id);
    recordEvent(db, id, { actor, from: t.status, to: 'done', detail: commit ? `commit=${commit}` : null });
  });
  const out = getTask(db, id);
  if (wantsJson(args)) emitJson({ task: out });
  else process.stdout.write(`done ${out.id}\n`);
}

function setStatus(db, args, to, { from, extra } = {}) {
  const id = args.pos[0];
  if (!id) die(`${to} needs a task id`);
  const t = mustTask(db, id);
  if (from && !from.includes(t.status)) die(`${id} is ${t.status}`, 3);
  const actor = resolveActor(args.one('agent'));
  const at = nowIso();
  withTx(db, () => {
    db.prepare('UPDATE tasks SET status=?, updated=? WHERE id=?').run(to, at, id);
    extra?.(db, t, args, at);
    recordEvent(db, id, { actor, from: t.status, to, detail: args.one('reason') ?? args.one('note') ?? null });
  });
  const out = getTask(db, id);
  if (wantsJson(args)) emitJson({ task: out });
  else process.stdout.write(`${out.id} -> ${to}\n`);
}

function cmdBlock(db, args) {
  const on = args.one('on');
  if (!on) die('block needs --on <task-id|text>');
  const id = args.pos[0];
  if (!id) die('block needs a task id');
  mustTask(db, id);
  setStatus(db, args, 'blocked', {
    from: ['ready', 'claimed', 'review'],
    extra: (d) => d.prepare('UPDATE tasks SET blocked_on=? WHERE id=?').run(on, id),
  });
}

function cmdUnblock(db, args) {
  const id = args.pos[0];
  if (!id) die('unblock needs a task id');
  mustTask(db, id);
  setStatus(db, args, 'ready', {
    from: ['blocked', 'failed'],
    extra: (d) =>
      d
        .prepare("UPDATE tasks SET blocked_on=NULL, agent=NULL, lease_until=NULL, worktree=NULL, branch=NULL WHERE id=?")
        .run(id),
  });
}

function cmdReopen(db, args) {
  const id = args.pos[0];
  if (!id) die('reopen needs a task id');
  mustTask(db, id);
  setStatus(db, args, 'ready', {
    from: ['done', 'cancelled', 'failed', 'claimed', 'review', 'blocked'],
    extra: (d) =>
      d
        .prepare(
          "UPDATE tasks SET agent=NULL, lease_until=NULL, worktree=NULL, branch=NULL, blocked_on=NULL, finished_at=NULL WHERE id=?"
        )
        .run(id),
  });
}

function cmdFail(db, args) {
  const id = args.pos[0];
  if (!id) die('fail needs a task id');
  mustTask(db, id);
  setStatus(db, args, 'failed', {
    from: ['ready', 'claimed', 'review'],
    extra: (d, _t, _a, at) => d.prepare('UPDATE tasks SET finished_at=? WHERE id=?').run(at, id),
  });
}

function cmdCancel(db, args) {
  const id = args.pos[0];
  if (!id) die('cancel needs a task id');
  mustTask(db, id);
  setStatus(db, args, 'cancelled', {
    from: ['ready', 'claimed', 'review', 'blocked', 'failed'],
    extra: (d, _t, _a, at) => d.prepare('UPDATE tasks SET finished_at=? WHERE id=?').run(at, id),
  });
}

function cmdReclaim(db, args) {
  const now = nowIso();
  const stale = plainAll(
    db.prepare("SELECT * FROM tasks WHERE status='claimed' AND lease_until IS NOT NULL AND lease_until < ? ORDER BY seq").all(now)
  );
  if (args.bools.has('dry-run')) {
    if (wantsJson(args)) emitJson({ reclaimed: stale.map((t) => t.id), tasks: stale });
    else if (!stale.length) process.stdout.write('no expired leases\n');
    else process.stdout.write(`would reclaim:\n${renderTaskList(stale)}\n`);
    return;
  }
  const actor = resolveActor(args.one('agent'));
  withTx(db, () => {
    for (const t of stale) {
      db.prepare(
        "UPDATE tasks SET status='ready', agent=NULL, lease_until=NULL, worktree=NULL, branch=NULL, updated=? WHERE id=?"
      ).run(now, t.id);
      recordEvent(db, t.id, {
        actor,
        from: 'claimed',
        to: 'ready',
        detail: `reclaimed expired lease (was ${t.agent ?? 'unknown'}${t.branch ? ` on ${t.branch}` : ''})`,
      });
    }
  });
  const ids = stale.map((t) => t.id);
  const after = ids.length
    ? plainAll(db.prepare(`SELECT * FROM tasks WHERE id IN (${ids.map(() => '?').join(',')}) ORDER BY seq`).all(...ids))
    : [];
  if (wantsJson(args)) emitJson({ reclaimed: ids, tasks: after });
  else process.stdout.write(after.length ? `reclaimed ${after.length} task(s)\n${renderTaskList(after)}\n` : 'no expired leases\n');
}

function cmdShow(db, args) {
  const id = args.pos[0];
  if (!id) die('show needs a task id');
  const t = mustTask(db, id);
  const deps = depsOf(db, id);
  const events = plainAll(
    db.prepare('SELECT * FROM events WHERE task_id=? ORDER BY id').all(id)
  );
  if (wantsJson(args)) {
    emitJson({ task: t, deps, ready: isReady(db, id), events });
    return;
  }
  const out = [renderTask(t)];
  if (t.body) out.push('', t.body.trimEnd());
  if (deps.length) out.push('', 'deps: ' + deps.map((d) => `${d.id}(${d.status})`).join(' '));
  if (events.length) {
    out.push('', 'history:');
    for (const e of events) {
      out.push(`  ${e.at}  ${e.from_status ?? '-'} -> ${e.to_status ?? '-'}  ${e.actor ?? ''}  ${e.detail ?? ''}`.trimEnd());
    }
  }
  process.stdout.write(out.join('\n') + '\n');
}

function cmdEvents(db, args) {
  const id = args.pos[0];
  const params = [];
  let sql = 'SELECT * FROM events';
  if (id) {
    sql += ' WHERE task_id = ?';
    params.push(id);
  }
  sql += ' ORDER BY id DESC';
  if (args.one('limit') !== undefined) {
    sql += ' LIMIT ?';
    params.push(intArg(args.one('limit'), 'limit', { min: 1 }));
  }
  const rows = plainAll(db.prepare(sql).all(...params));
  if (wantsJson(args)) emitJson(rows);
  else if (!rows.length) process.stdout.write('(no events)\n');
  else for (const e of rows) process.stdout.write(`${e.at}  ${e.task_id}  ${e.from_status ?? '-'} -> ${e.to_status ?? '-'}  ${e.actor ?? ''}  ${e.detail ?? ''}\n`.trimEnd() + '\n');
}

function cmdStats(db, args) {
  const counts = Object.fromEntries(STATUSES.map((s) => [s, 0]));
  for (const r of db.prepare('SELECT status, COUNT(*) n FROM tasks GROUP BY status').all()) counts[r.status] = r.n;
  const total = Object.values(counts).reduce((a, b) => a + b, 0);
  const ready = readyTasks(db).length;
  const blockedByDeps = db
    .prepare(
      `SELECT COUNT(*) n FROM tasks t WHERE t.status='ready' AND EXISTS (
         SELECT 1 FROM deps d JOIN tasks p ON p.id=d.dep_id
          WHERE d.task_id=t.id AND p.status NOT IN ${SATISFIED})`
    )
    .get().n;
  const expired = db
    .prepare("SELECT COUNT(*) n FROM tasks WHERE status='claimed' AND lease_until IS NOT NULL AND lease_until < ?")
    .get(nowIso()).n;
  const data = { total, ...counts, actionable_ready: ready, waiting_on_deps: blockedByDeps, expired_leases: expired };
  if (wantsJson(args)) emitJson(data);
  else {
    process.stdout.write(`total ${total}\n`);
    for (const s of STATUSES) process.stdout.write(`  ${pad(s, 10)} ${counts[s]}\n`);
    process.stdout.write(`\n  ${pad('actionable', 10)} ${ready}   (ready + unblocked)\n`);
    process.stdout.write(`  ${pad('dep-waiting', 10)} ${blockedByDeps}\n`);
    if (expired) process.stdout.write(`  ${pad('expired', 10)} ${expired}   (run: tasks reclaim)\n`);
  }
}

function cmdExport(db, args) {
  const status = args.one('status') ?? 'open';
  if (status !== 'open' && status !== 'all' && !STATUSES.includes(status)) {
    die(`--status must be open, all, or one of: ${STATUSES.join(', ')}`);
  }
  const params = [];
  let where = '';
  if (status === 'open') {
    where = `WHERE status IN (${OPEN_STATUSES.map(() => '?').join(',')})`;
    params.push(...OPEN_STATUSES);
  } else if (status !== 'all') {
    where = 'WHERE status = ?';
    params.push(status);
  }
  const rows = plainAll(db.prepare(`SELECT * FROM tasks ${where} ORDER BY priority, seq`).all(...params));
  const label = status === 'open' ? 'Open' : status === 'all' ? 'Total' : status;
  const lines = ['# Task queue', '', `${label}: ${rows.length}`, '', renderTaskList(rows, { md: true }), ''];
  process.stdout.write(lines.join('\n'));
}

function cmdWhere(db, args) {
  const p = db.prepare('PRAGMA database_list').get();
  if (wantsJson(args)) emitJson({ db: p.file, git_common_dir: repoCommonDir() });
  else process.stdout.write(`${p.file}\n`);
}

// ---------------------------------------------------------------------------
// dispatch

function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.bools.has('help')) {
    process.stdout.write(HELP + '\n');
    return;
  }
  const dbPath = resolveDbPath(args.one('db'));
  if (args.bools.has('version')) {
    process.stdout.write(`${PROG} (schema 1) — ${dbPath}\n`);
    return;
  }
  const db = openDb(dbPath);
  const cmd = args.pos.shift() ?? 'list';

  switch (cmd) {
    case 'add': return cmdAdd(db, args);
    case 'edit': return cmdEdit(db, args);
    case 'list':
    case 'ls': return cmdList(db, args);
    case 'ready': return cmdReady(db, args);
    case 'next': return cmdNext(db, args);
    case 'claim': return cmdClaim(db, args);
    case 'submit':
    case 'review': return cmdSubmit(db, args);
    case 'done': return cmdDone(db, args);
    case 'block': return cmdBlock(db, args);
    case 'unblock': return cmdUnblock(db, args);
    case 'reopen': return cmdReopen(db, args);
    case 'fail': return cmdFail(db, args);
    case 'cancel': return cmdCancel(db, args);
    case 'reclaim': return cmdReclaim(db, args);
    case 'show': return cmdShow(db, args);
    case 'events': return cmdEvents(db, args);
    case 'stats': return cmdStats(db, args);
    case 'export': return cmdExport(db, args);
    case 'where':
    case 'path': return cmdWhere(db, args);
    case 'help': process.stdout.write(HELP + '\n'); return;
    default: die(`unknown command: ${cmd}\n\n${HELP}`);
  }
}

main();
