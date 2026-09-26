#!/usr/bin/env node
// Spatial Engine task board.
//
// A small, read-only web app over the development task queue
// (tools/tasks/tasks.mjs): queue state, status counts and the event log,
// pushed to the browser over Server-Sent Events. It is development
// infrastructure — no ADR, no `src/` code, no product surface — and it is wired
// into the Aspire AppHost as a resource so `aspire start` brings it up with a
// link on the dashboard.
//
// It never writes: the CLI owns every transition, and this only reads the same
// shared SQLite database. Run it directly with `eng/board`, or let Aspire pick
// the port (it sets PORT / a URL env var, see resolvePort).

import { DatabaseSync } from 'node:sqlite';
import { execFileSync } from 'node:child_process';
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
