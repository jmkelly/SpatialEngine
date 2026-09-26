// Tests for the development task board (tools/tasks-board/server.mjs).
//
// Black-box like tools/tasks/tasks.test.mjs: a throwaway queue database is
// seeded through the real CLI, then the board server is started on an ephemeral
// port and exercised over HTTP. Run with:
//
//   node --test tools/tasks-board/board.test.mjs

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { createBoard, resolvePort, snapshotOf } from './server.mjs';

const CLI = fileURLToPath(new URL('../tasks/tasks.mjs', import.meta.url));

function sandbox() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'tasks-board-test-'));
  return path.join(dir, 'tasks.db');
}

function cli(db, args) {
  const res = spawnSync(process.execPath, [CLI, '--db', db, ...args], {
    encoding: 'utf8',
    env: { ...process.env, SPATIAL_TASK_AGENT: 'tester' },
  });
  assert.equal(res.status, 0, `\`tasks ${args.join(' ')}\` failed: ${res.stderr}`);
  return res.stdout ? JSON.parse(res.stdout) : undefined;
}

async function withBoard(db, fn) {
  const board = createBoard({ dbPath: db, pollMs: 25 });
  await new Promise((resolve) => board.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${board.port}`;
  try {
    await fn(base);
  } finally {
    await board.close();
  }
}

test('snapshot reports the queue, the stats and the recent event log', async () => {
  const db = sandbox();
  const a = cli(db, ['add', 'first task', '--area', 'host', '--priority', '1', '--json']).task;
  const b = cli(db, ['add', 'second task', '--area', 'workbench', '--json']).task;
  cli(db, ['claim', a.id, '--agent', 'tester']);

  const board = createBoard({ dbPath: db, pollMs: 1000 });
  try {
    const snap = snapshotOf(board);
    assert.equal(snap.db, db);
    assert.equal(snap.tasks.length, 2);
    assert.deepEqual(
      snap.tasks.map((t) => t.id),
      [a.id, b.id],
      'tasks are ordered by priority then seq'
    );
    const claimed = snap.tasks.find((t) => t.id === a.id);
    assert.equal(claimed.status, 'claimed');
    assert.equal(claimed.agent, 'tester');
    assert.ok(claimed.lease_until, 'a claimed task carries its lease');
    assert.equal(claimed.lease_expired, false);
    assert.equal(snap.stats.claimed, 1);
    assert.equal(snap.stats.ready, 1);
    assert.equal(snap.stats.actionable_ready, 1);
    assert.equal(snap.stats.total, 2);

    // Newest first, with the creation and the claim.
    const details = snap.events.map((e) => e.detail);
    assert.deepEqual(details.slice(0, 2), ['claimed', 'created']);
    assert.equal(snap.events[0].task_id, a.id);
    assert.ok(snap.cursor > 0, 'the cursor is the newest event id');
  } finally {
    board.close();
  }
});

test('a dependency marks its dependant as not actionable', async () => {
  const db = sandbox();
  const first = cli(db, ['add', 'first', '--json']).task;
  const second = cli(db, ['add', 'second', '--dep', first.id, '--json']).task;

  const board = createBoard({ dbPath: db, pollMs: 1000 });
  try {
    const snap = snapshotOf(board);
    const dependant = snap.tasks.find((t) => t.id === second.id);
    assert.deepEqual(dependant.deps, [{ id: first.id, title: 'first', status: 'ready' }]);
    assert.deepEqual(dependant.unmet_deps, [first.id]);
    assert.equal(snap.stats.ready, 2, 'both rows are still status ready');
    assert.equal(snap.stats.actionable_ready, 1, 'only the first is actionable');
    assert.equal(snap.stats.waiting_on_deps, 1);
  } finally {
    board.close();
  }
});

test('an expired lease is flagged so the board shows stale claims', async () => {
  const db = sandbox();
  const t = cli(db, ['add', 'stale', '--json']).task;
  cli(db, ['claim', t.id, '--agent', 'tester', '--lease', '1']);

  const board = createBoard({ dbPath: db, pollMs: 1000 });
  try {
    // Wind the lease back rather than waiting for it to lapse.
    const { DatabaseSync } = await import('node:sqlite');
    const raw = new DatabaseSync(db);
    raw.exec("UPDATE tasks SET lease_until = '2000-01-01T00:00:00.000Z'");
    raw.close();

    const snap = snapshotOf(board);
    const row = snap.tasks.find((x) => x.id === t.id);
    assert.equal(row.lease_expired, true);
    assert.equal(snap.stats.expired_leases, 1);
  } finally {
    board.close();
  }
});

test('/api/snapshot serves JSON and /api/stream pushes changes as they happen', async () => {
  const db = sandbox();
  const t = cli(db, ['add', 'streamed', '--json']).task;

  await withBoard(db, async (base) => {
    const res = await fetch(`${base}/api/snapshot`);
    assert.equal(res.status, 200);
    const body = await res.json();
    assert.equal(body.tasks[0].id, t.id);

    const controller = new AbortController();
    const stream = await fetch(`${base}/api/stream`, { signal: controller.signal });
    assert.equal(stream.headers.get('content-type'), 'text/event-stream');

    const events = [];
    const reader = stream.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    const pump = (async () => {
      try {
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });
          let idx;
          while ((idx = buffer.indexOf('\n\n')) !== -1) {
            const frame = buffer.slice(0, idx);
            buffer = buffer.slice(idx + 2);
            const name = /^event: (.+)$/m.exec(frame)?.[1];
            const data = /^data: (.+)$/m.exec(frame)?.[1];
            if (name && data) events.push({ name, data: JSON.parse(data) });
          }
        }
      } catch {
        /* aborted */
      }
    })();

    // Wait for the opening snapshot frame, then mutate the queue.
    const deadline = Date.now() + 5000;
    while (!events.length && Date.now() < deadline) await sleep(25);
    assert.equal(events[0]?.name, 'snapshot');
    assert.equal(events[0].data.tasks[0].id, t.id);

    cli(db, ['claim', t.id, '--agent', 'tester']);
    while (events.length < 2 && Date.now() < deadline) await sleep(25);

    controller.abort();
    await pump;

    const change = events.find((e) => e.name === 'change');
    assert.ok(change, 'the claim was pushed without a page refresh');
    assert.equal(change.data.tasks[0].status, 'claimed');
    assert.equal(change.data.stats.claimed, 1);
    assert.ok(
      change.data.events.some((e) => e.detail === 'claimed'),
      'the new log line rides along with the change'
    );
    assert.ok(change.data.cursor > events[0].data.cursor, 'the cursor advances');
  });
});

test('the board serves its own page and answers a health check', async () => {
  const db = sandbox();
  cli(db, ['add', 'served', '--json']);
  await withBoard(db, async (base) => {
    const page = await fetch(`${base}/`);
    assert.equal(page.status, 200);
    assert.match(page.headers.get('content-type'), /text\/html/);
    const html = await page.text();
    assert.match(html, /<div id="app"/);

    const health = await fetch(`${base}/healthz`);
    assert.equal(health.status, 200);
    assert.equal((await health.json()).tasks, 1);
  });
});

test('a missing database is reported, not crashed on', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'tasks-board-empty-'));
  const board = createBoard({ dbPath: path.join(dir, 'nested', 'tasks.db'), pollMs: 1000 });
  try {
    const snap = snapshotOf(board);
    assert.equal(snap.tasks.length, 0);
    assert.equal(snap.stats.total, 0);
  } finally {
    board.close();
  }
});

test('the port comes from Aspire (PORT or a URL env var), not a hardcoded one', () => {
  assert.equal(resolvePort({ PORT: '5123' }, 1), 5123);
  assert.equal(resolvePort({ PORT: 'http://localhost:5124' }, 1), 5124);
  assert.equal(resolvePort({ ASPNETCORE_URLS: 'http://127.0.0.1:5199' }, 1), 5199);
  assert.equal(resolvePort({ urls: 'http://0.0.0.0:5200' }, 1), 5200);
  assert.equal(resolvePort({}, 5178), 5178, 'falls back to the default');
  assert.equal(resolvePort({ PORT: 'nonsense' }, 5178), 5178, 'ignores junk');
});

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
