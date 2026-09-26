#!/usr/bin/env node
// Spatial Engine task board.
//
// A small, read-only web app over the development task queue
// (tools/tasks/tasks.mjs): queue state, status counts and the event log,
// refreshed by the browser polling a snapshot. It is development
// infrastructure — no ADR, no `src/` code, no product surface — and it is wired
// into the Aspire AppHost as a resource so `aspire start` brings it up with a
// link on the dashboard.
//
// It never writes to the queue: the CLI owns every transition. The one action
// it offers, "investigate", does not write either — it hands the task ids to
// `paseo run`, and that agent drives the CLI like any other. Run it directly
// with `eng/board`, or let Aspire pick the port (it sets PORT, see
// resolvePort).

import { DatabaseSync } from 'node:sqlite';
import { execFileSync, spawn } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(HERE, '..', '..');
const PUBLIC_DIR = path.join(HERE, 'public');
const CLI = path.join(REPO_ROOT, 'tools', 'tasks', 'tasks.mjs');

const STATUSES = ['ready', 'claimed', 'review', 'done', 'blocked', 'failed', 'cancelled'];
const OPEN_STATUSES = ['ready', 'claimed', 'review', 'blocked'];
const SATISFIED = "('done','cancelled')";
const DEFAULT_PORT = 5178;
const DEFAULT_POLL_MS = 1000;
const DEFAULT_EVENT_LOG = 200;
const HEARTBEAT_MS = 15000;

const nowIso = () => new Date().toISOString();
const plainAll = (rows) => rows.map((r) => Object.assign({}, r));

// The CLI owns database-path resolution (--db, $SPATIAL_TASKS_DB, the shared
// git-common-dir, the XDG fallback). Ask it rather than reimplementing the
// precedence here, so the board can never point somewhere the queue is not.
export function resolveDbPath() {
  const out = execFileSync(process.execPath, [CLI, 'where', '--json'], {
    cwd: REPO_ROOT,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'inherit'],
  });
  return JSON.parse(out).db;
}

// Aspire assigns the port; depending on the integration it arrives as a bare
// number (PORT) or as a URL (ASPNETCORE_URLS / urls). Take the port either way,
// and never crash on a value we do not understand.
export function resolvePort(env = process.env, fallback = DEFAULT_PORT) {
  for (const key of ['PORT', 'urls', 'ASPNETCORE_URLS']) {
    const raw = (env[key] ?? '').trim();
    if (!raw) continue;
    const n = /^\d+$/.test(raw)
      ? Number(raw)
      : Number(/:(\d{2,5})(?:\/|$)/.exec(raw)?.[1]);
    if (Number.isInteger(n) && n > 0 && n < 65536) return n;
  }
  return fallback;
}

function openDb(dbPath) {
  fs.mkdirSync(path.dirname(dbPath), { recursive: true });
  const db = new DatabaseSync(dbPath);
  // Same pragmas as the CLI: a WAL reader that a writer is using concurrently,
  // waiting rather than failing, is the whole point of polling a live queue.
  db.exec('PRAGMA journal_mode=WAL;');
  db.exec('PRAGMA busy_timeout=5000;');
  return db;
}

function taskRows(db) {
  return plainAll(db.prepare('SELECT * FROM tasks ORDER BY priority, seq').all());
}

function depsOf(db, id) {
  return plainAll(
    db.prepare(
      `SELECT d.dep_id AS id, COALESCE(p.title,'') AS title, COALESCE(p.status,'missing') AS status
         FROM deps d LEFT JOIN tasks p ON p.id = d.dep_id
        WHERE d.task_id = ? ORDER BY d.dep_id`
    ).all(id)
  );
}

function unmetDeps(db, id) {
  return db
    .prepare(
      `SELECT d.dep_id
         FROM deps d JOIN tasks p ON p.id = d.dep_id
        WHERE d.task_id = ? AND p.status NOT IN ${SATISFIED}
        ORDER BY d.dep_id`
    )
    .all(id)
    .map((r) => r.dep_id);
}

function statsOf(db, tasks) {
  const counts = Object.fromEntries(STATUSES.map((s) => [s, 0]));
  for (const t of tasks) if (counts[t.status] !== undefined) counts[t.status] += 1;
  const now = nowIso();
  return {
    total: tasks.length,
    ...counts,
    open: tasks.filter((t) => OPEN_STATUSES.includes(t.status)).length,
    actionable_ready: tasks.filter((t) => t.status === 'ready' && t.unmet_deps.length === 0).length,
    waiting_on_deps: tasks.filter((t) => t.status === 'ready' && t.unmet_deps.length > 0).length,
    expired_leases: tasks.filter((t) => t.lease_expired).length,
    updated: now,
  };
}

function eventLog(db, limit) {
  return plainAll(db.prepare('SELECT * FROM events ORDER BY id DESC LIMIT ?').all(limit)).reverse();
}

function cursorOf(db) {
  return db.prepare('SELECT COALESCE(MAX(id), 0) AS n FROM events').get().n;
}

// Cheap change signature: polling this every second is the whole update loop,
// so it must not touch every row.
function signatureOf(db) {
  const row = db
    .prepare(
      `SELECT COUNT(*) AS n, COALESCE(MAX(updated), '') AS updated,
              COALESCE(SUM(seq), 0) AS seq
         FROM tasks`
    )
    .get();
  return `${row.n}:${row.seq}:${row.updated}:${cursorOf(db)}`;
}

/**
 * Read the queue. The returned object is a plain snapshot: tasks, counts and
 * the newest-last event log, plus the `cursor` clients resume from.
 */
export function snapshotOf(board, { eventLimit = DEFAULT_EVENT_LOG } = {}) {
  const db = board.db;
  const now = nowIso();
  const raw = taskRows(db);
  const tasks = raw.map((t) => ({
    id: t.id,
    seq: t.seq,
    title: t.title,
    area: t.area,
    priority: t.priority,
    status: t.status,
    agent: t.agent,
    lease_until: t.lease_until,
    lease_expired: !!t.lease_until && t.lease_until < now,
    blocked_on: t.blocked_on,
    commit_sha: t.commit_sha,
    pr: t.pr,
    worktree: t.worktree,
    branch: t.branch,
    note: t.note,
    created: t.created,
    updated: t.updated,
    claimed_at: t.claimed_at,
    finished_at: t.finished_at,
    deps: depsOf(db, t.id),
    unmet_deps: unmetDeps(db, t.id),
  }));
  return {
    db: board.dbPath,
    now,
    cursor: cursorOf(db),
    stats: statsOf(db, tasks),
    tasks,
    events: eventLog(db, eventLimit),
  };
}

// The change frame: everything the client needs to re-render, plus only the
// events it has not seen.
function changeOf(board, sinceCursor) {
  const snap = snapshotOf(board);
  const fresh = board.db
    .prepare('SELECT * FROM events WHERE id > ? ORDER BY id')
    .all(sinceCursor)
    .map((r) => Object.assign({}, r));
  return { cursor: snap.cursor, now: snap.now, stats: snap.stats, tasks: snap.tasks, events: fresh };
}

// ---------------------------------------------------------------------------
// investigate: hand task ids to a Paseo agent
//
// The board stays out of the queue's write path. "Investigate" is a request for
// a second opinion, not a transition: the spawned agent reads the task, looks
// for salvageable work on its branch, and then drives the same `eng/tasks` CLI
// a human would. Nothing here touches the database.

const MAX_BODY = 64 * 1024;
const INVESTIGATE_TIMEOUT_MS = 20_000;
const PROVIDER_LOOKUP_TIMEOUT_MS = 10_000;

/**
 * Which provider to launch the triage agent with.
 *
 * `paseo run` refuses to guess (MISSING_PROVIDER), and hardcoding a model here
 * would rot the moment the human switches. So: an explicit override wins, and
 * otherwise the board reuses the provider this project was actually last worked
 * on, read from Paseo's own agent list. Failing that, say what to set rather
 * than guessing.
 */
export function resolveProvider(env = process.env, run = execFileSync) {
  const override = (env.TASKS_BOARD_PROVIDER ?? '').trim();
  if (override) return override;

  let agents;
  try {
    agents = JSON.parse(
      run('paseo', ['ls', '--json'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] })
    );
  } catch {
    agents = [];
  }
  const here = path.basename(REPO_ROOT);
  const match = (Array.isArray(agents) ? agents : []).find(
    (a) => a?.provider && (a.cwd ?? '').replace(/^~/, '').endsWith(here)
  );
  if (match) return match.provider;
  throw new Error(
    'no provider to launch with: set TASKS_BOARD_PROVIDER (e.g. `claude/opus`), ' +
      'or start an agent in this project once so the board can reuse its provider'
  );
}

// Paseo reports failures as a JSON body on stderr; surfacing that raw is noise,
// so lift out the message and keep the code for the caller.
function paseoError(stderr) {
  const raw = stderr.trim();
  try {
    const parsed = JSON.parse(raw);
    const err = parsed?.error ?? parsed;
    if (typeof err === 'string') return err;
    if (err?.message) return `${err.message}${err.code ? ` (${err.code})` : ''}`;
  } catch {
    // Not JSON: fall through to the raw text.
  }
  return raw;
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on('data', (chunk) => {
      size += chunk.length;
      if (size > MAX_BODY) {
        reject(new Error('request body too large'));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => {
      const raw = Buffer.concat(chunks).toString('utf8').trim();
      if (!raw) return resolve({});
      try {
        resolve(JSON.parse(raw));
      } catch {
        reject(new Error('request body is not valid JSON'));
      }
    });
    req.on('error', reject);
  });
}

// Only the browser tab this server served may call an endpoint that spends
// money and starts a process. The custom header is the guard: a cross-origin
// page cannot set one without a CORS preflight, and this server never answers
// preflights, so a hostile page cannot reach it. The Host check keeps a rebound
// DNS name from reaching it either.
function isSameOriginCaller(req) {
  const host = (req.headers.host ?? '').replace(/:\d+$/, '').replace(/^\[|\]$/g, '');
  const local = host === '127.0.0.1' || host === 'localhost' || host === '::1';
  return local && req.headers['x-taskboard'] === '1';
}

function factsAbout(task) {
  const lines = [
    `  ${task.id}  ${task.title}`,
    `    status=${task.status} area=${task.area || '?'} priority=${task.priority}`,
    `    claimed by ${task.agent ?? '?'} at ${task.claimed_at ?? '?'}` +
      (task.lease_until ? `, lease lapsed at ${task.lease_until}` : ''),
  ];
  for (const [label, value] of [
    ['branch', task.branch],
    ['worktree', task.worktree],
    ['commit', task.commit_sha],
    ['pr', task.pr ? `#${task.pr}` : null],
    ['blocked on', task.blocked_on],
  ]) {
    if (value) lines.push(`    ${label}=${value}`);
  }
  if (task.note) lines.push(`    note: ${task.note}`);
  return lines.join('\n');
}

export function investigationPrompt(tasks) {
  const ids = tasks.map((t) => t.id).join(', ');
  return [
    'You are investigating lapsed claims in the Spatial Engine development task queue',
    `(${REPO_ROOT}). These tasks are still in status \`claimed\` but their lease has expired,`,
    'so the agent that took them stopped without submitting anything. Nobody knows whether the',
    'work was done, partly done, or never started.',
    '',
    'Tasks to investigate:',
    ...tasks.map(factsAbout),
    '',
    'For each task, establish what actually happened before deciding anything:',
    '',
    '  1. `eng/tasks show <id>` for the full body, dependencies and event history.',
    '  2. If it records a branch, worktree, commit or PR, check whether that work exists:',
    '     `git log`, `git branch -a`, `gh pr list`. Salvageable work must not be redone.',
    '  3. Only then decide, and act with the CLI:',
    '       - the work exists but was never handed off -> `eng/tasks submit <id> --commit <sha> --pr <n>`',
    '       - nothing usable was produced             -> `eng/tasks reclaim` is NOT enough on its',
    '         own, so re-queue it for a fresh agent and say so in your report',
    '       - the task is obsolete or already done    -> `eng/tasks cancel <id> --note "<why>"`',
    '',
    'Rules that are not negotiable:',
    '  - The queue is shared state. Do not edit tasks.db directly; use `eng/tasks`.',
    '  - Never mark a task `done` yourself: that is `eng/tasks done`, and only after',
    '    `eng/verify.sh` is green on main. Claiming, investigating and cancelling are yours.',
    '  - Do not delete or rewrite history; `cancel` with a note is the honest record.',
    '  - Do not start implementing the underlying work. You are triaging, not finishing.',
    '',
    `Report per task: what you found, the evidence (branch, commit or PR), and the action you`,
    'took or recommend. Be conservative — when the evidence is ambiguous, leave the task',
    `claimed and say so rather than guessing. The task ids are: ${ids}.`,
  ].join('\n');
}

/**
 * Start a background Paseo agent to triage the given task ids. Resolves with
 * the agent descriptor the daemon returns; it never waits for the work itself.
 */
export function investigate(db, ids, { provider } = {}) {
  if (!Array.isArray(ids) || ids.length === 0) {
    return Promise.reject(new Error('no task ids given'));
  }
  const wanted = [...new Set(ids.map(String))];
  const tasks = wanted
    .map((id) => taskRows(db).find((t) => t.id === id))
    .filter(Boolean);
  if (tasks.length === 0) return Promise.reject(new Error('no such task'));
  const open = tasks.filter((t) => OPEN_STATUSES.includes(t.status));
  if (open.length === 0) return Promise.reject(new Error('no open task among those ids'));

  const prompt = investigationPrompt(open);
  const title = `Investigate ${open.map((t) => t.id).join(', ')}`;

  let args;
  try {
    args = ['run', '--json', '--background', '--provider', provider ?? resolveProvider(), '--title', title, '--cwd', REPO_ROOT, '--label', 'taskboard=investigate', prompt];
  } catch (err) {
    return Promise.reject(err);
  }

  return new Promise((resolve, reject) => {
    const child = spawn('paseo', args, { cwd: REPO_ROOT, stdio: ['ignore', 'pipe', 'pipe'] });

    let out = '';
    let err = '';
    const timer = setTimeout(() => {
      child.kill('SIGTERM');
      reject(new Error('paseo did not answer in time; check `paseo ls`'));
    }, INVESTIGATE_TIMEOUT_MS);

    child.stdout.on('data', (c) => (out += c));
    child.stderr.on('data', (c) => (err += c));
    child.on('error', (e) => {
      clearTimeout(timer);
      reject(new Error(`cannot run paseo: ${e.message}`));
    });
    child.on('close', (code) => {
      clearTimeout(timer);
      if (code !== 0) return reject(new Error(paseoError(err) || `paseo exited ${code}`));
      let agent;
      try {
        const parsed = JSON.parse(out);
        agent = Array.isArray(parsed) ? parsed[0] : parsed;
      } catch {
        return reject(new Error('paseo returned no agent descriptor'));
      }
      if (!agent?.id) return reject(new Error('paseo returned no agent id'));
      resolve({
        provider,
        agentId: agent.id,
        shortId: agent.shortId ?? agent.id.slice(0, 8),
        status: agent.status ?? 'running',
        title,
        tasks: open.map((t) => t.id),
      });
    });
  });
}

const CONTENT_TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.json': 'application/json; charset=utf-8',
};

function sendJson(res, status, body) {
  const text = JSON.stringify(body);
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(text),
    'cache-control': 'no-store',
  });
  res.end(text);
}

function serveStatic(res, name) {
  const file = path.join(PUBLIC_DIR, name);
  if (!file.startsWith(PUBLIC_DIR + path.sep) && file !== path.join(PUBLIC_DIR, 'index.html')) {
    res.writeHead(403).end('forbidden');
    return;
  }
  let body;
  try {
    body = fs.readFileSync(file);
  } catch {
    res.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' }).end('not found');
    return;
  }
  res.writeHead(200, {
    'content-type': CONTENT_TYPES[path.extname(file)] ?? 'application/octet-stream',
    'content-length': body.length,
    'cache-control': 'no-store',
  });
  res.end(body);
}

/**
 * The board: an open read-only view of the queue, a poll loop that turns queue
 * writes into pushes, and an HTTP surface (page, snapshot, SSE stream).
 */
export function createBoard({
  dbPath = resolveDbPath(),
  pollMs = DEFAULT_POLL_MS,
  eventLimit = DEFAULT_EVENT_LOG,
} = {}) {
  const board = {
    dbPath,
    pollMs,
    eventLimit,
    db: openDb(dbPath),
    clients: new Set(),
    timer: null,
    signature: null,
    server: null,
    port: 0,
  };

  const push = (frame) => {
    const text = `event: ${frame.name}\nid: ${frame.data.cursor ?? 0}\ndata: ${JSON.stringify(frame.data)}\n\n`;
    for (const res of board.clients) {
      if (!res.writableEnded) res.write(text);
    }
  };

  const tick = () => {
    try {
      const signature = signatureOf(board.db);
      if (signature === board.signature) return;
      const since = board.clients.size
        ? Math.min(...[...board.clients].map((c) => c.cursor))
        : board.signature === null
          ? cursorOf(board.db)
          : 0;
      board.signature = signature;
      if (since === cursorOf(board.db) && board.clients.size === 0) return;
      const frame = { name: since > 0 ? 'change' : 'snapshot', data: changeOf(board, since) };
      if (frame.name === 'change') {
        for (const res of board.clients) res.cursor = frame.data.cursor;
      }
      push(frame);
    } catch (err) {
      process.stderr.write(`taskboard: poll failed: ${err?.message ?? err}\n`);
    }
  };

  board.handle = (req, res) => {
    const url = new URL(req.url ?? '/', 'http://localhost');
    const route = url.pathname;

    if (route === '/api/snapshot') {
      try {
        sendJson(res, 200, snapshotOf(board, { eventLimit }));
      } catch (err) {
        sendJson(res, 500, { error: String(err?.message ?? err) });
      }
      return;
    }

    if (route === '/api/investigate' && req.method === 'POST') {
      if (!isSameOriginCaller(req)) {
        return sendJson(res, 403, { error: 'same-origin calls only' });
      }
      readBody(req)
        .then((body) => investigate(board.db, body.ids))
        .then((result) => sendJson(res, 202, result))
        .catch((err) => sendJson(res, 400, { error: String(err?.message ?? err) }));
      return;
    }

    if (route === '/healthz') {
      try {
        const snap = snapshotOf(board);
        sendJson(res, 200, { status: 'healthy', db: snap.db, tasks: snap.tasks.length, cursor: snap.cursor });
      } catch (err) {
        sendJson(res, 503, { status: 'unhealthy', error: String(err?.message ?? err) });
      }
      return;
    }

    if (route === '/api/stream') {
      res.writeHead(200, {
        'content-type': 'text/event-stream; charset=utf-8',
        'cache-control': 'no-store',
        connection: 'keep-alive',
        'x-accel-buffering': 'no',
      });
      const opening = { name: 'snapshot', data: snapshotOf(board, { eventLimit }) };
      res.write(`retry: 2000\nevent: snapshot\nid: ${opening.data.cursor}\ndata: ${JSON.stringify(opening.data)}\n\n`);
      const client = { res, cursor: opening.data.cursor };
      board.clients.add(client);
      const heartbeat = setInterval(() => {
        if (!res.writableEnded) res.write(`: keep-alive ${Date.now()}\n\n`);
      }, HEARTBEAT_MS);
      const drop = () => {
        clearInterval(heartbeat);
        board.clients.delete(client);
      };
      req.on('close', drop);
      res.on('close', drop);
      return;
    }

    if (route === '/' || route === '/index.html') {
      serveStatic(res, 'index.html');
      return;
    }

    if (/^\/[A-Za-z0-9._-]+$/.test(route)) {
      serveStatic(res, route.slice(1));
      return;
    }

    res.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' }).end('not found');
  };

  board.listen = (port, host) =>
    new Promise((resolve, reject) => {
      board.server = http.createServer((req, res) => {
        try {
          board.handle(req, res);
        } catch (err) {
          sendJson(res, 500, { error: String(err?.message ?? err) });
        }
      });
      board.server.once('error', reject);
      board.server.listen(port, host, () => {
        board.port = board.server.address().port;
        board.signature = signatureOf(board.db);
        board.timer = setInterval(tick, board.pollMs);
        board.timer.unref?.();
        resolve(board);
      });
    });

  board.close = async () => {
    if (board.timer) clearInterval(board.timer);
    for (const { res } of board.clients) res.end();
    board.clients.clear();
    if (board.server) await new Promise((r) => board.server.close(r));
    board.db.close();
  };

  return board;
}

function main() {
  const board = createBoard({ pollMs: Number(process.env.TASKS_BOARD_POLL_MS) || DEFAULT_POLL_MS });
  const port = resolvePort();
  board
    .listen(port, process.env.TASKS_BOARD_HOST ?? '127.0.0.1')
    .then(() => {
      const snap = snapshotOf(board);
      process.stdout.write(
        `taskboard: http://${process.env.TASKS_BOARD_HOST ?? '127.0.0.1'}:${board.port} — ` +
          `${snap.tasks.length} tasks, cursor ${snap.cursor} (${snap.db})\n`
      );
    })
    .catch((err) => {
      process.stderr.write(`taskboard: ${err?.message ?? err}\n`);
      process.exit(1);
    });
  for (const sig of ['SIGINT', 'SIGTERM']) {
    process.on(sig, () => {
      board.close().then(() => process.exit(0));
    });
  }
}

if (import.meta.url === `file://${process.argv[1]}`) main();
