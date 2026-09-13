// Reproduction tests for the development task queue (tools/tasks/tasks.mjs).
//
// These are black-box tests: they spawn the CLI against a throwaway database,
// exactly as `eng/tasks` does. Run with:
//
//   node --test tools/tasks/tasks.test.mjs
//
// They are wired into the `node` CI job next to the other JavaScript suites.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const CLI = fileURLToPath(new URL('./tasks.mjs', import.meta.url));

function sandbox() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'tasks-test-'));
  return path.join(dir, 'tasks.db');
}

function run(db, args, { input } = {}) {
  return spawnSync(process.execPath, [CLI, '--db', db, ...args], {
    input: input ?? undefined,
    encoding: 'utf8',
    env: { ...process.env, SPATIAL_TASK_AGENT: 'tester' },
  });
}

function ok(db, args, opts) {
  const res = run(db, args, opts);
  assert.equal(res.status, 0, `expected exit 0 for \`${args.join(' ')}\`\nstderr: ${res.stderr}`);
  return res.stdout ? JSON.parse(res.stdout) : undefined;
}

// A failure must be a clean `tasks: ...` diagnostic on stderr with exit 1 — not
// an uncaught Node stack trace, and not a silent no-op.
function failsCleanly(db, args, match) {
  const res = run(db, args);
  assert.equal(res.status, 1, `expected exit 1 for \`${args.join(' ')}\`, got ${res.status}\nstdout: ${res.stdout}`);
  assert.ok(
    res.stderr.startsWith('tasks: '),
    `expected a clean \`tasks: ...\` error, got: ${res.stderr || '(empty stderr)'}`
  );
  assert.doesNotMatch(res.stderr, /\n\s+at /, `expected no stack trace, got: ${res.stderr}`);
  if (match) assert.match(res.stderr, match);
  return res;
}

test('add --body - reads the body from stdin', () => {
  const db = sandbox();
  const out = ok(db, ['add', 'one', '--body', '-'], { input: 'from stdin\n' });
  assert.equal(out.task.body, 'from stdin\n');
});

test('edit --body - reads the body from stdin', () => {
  const db = sandbox();
  ok(db, ['add', 'one']);
  const out = ok(db, ['edit', 'T-001', '--body', '-'], { input: 'edited body\n' });
  assert.equal(out.task.body, 'edited body\n');
  assert.equal(ok(db, ['show', 'T-001']).task.body, 'edited body\n');
});

test('list rejects a non-integer --limit instead of crashing', () => {
  const db = sandbox();
  ok(db, ['add', 'one']);
  failsCleanly(db, ['list', '--limit', 'abc'], /--limit/);
  failsCleanly(db, ['list', '--limit', '1.5'], /--limit/);
  failsCleanly(db, ['list', '--limit', '-1'], /--limit/);
});

test('ready and events reject a non-integer --limit', () => {
  const db = sandbox();
  ok(db, ['add', 'one']);
  failsCleanly(db, ['ready', '--limit', 'abc'], /--limit/);
  failsCleanly(db, ['events', '--limit', 'abc'], /--limit/);
});

test('--limit actually limits when valid', () => {
  const db = sandbox();
  ok(db, ['add', 'one']);
  ok(db, ['add', 'two']);
  ok(db, ['add', 'three']);
  assert.equal(ok(db, ['list', '--limit', '2']).length, 2);
});

test('claim rejects a non-integer --lease instead of crashing', () => {
  const db = sandbox();
  ok(db, ['add', 'one']);
  failsCleanly(db, ['claim', 'T-001', '--lease', 'abc'], /--lease/);
  failsCleanly(db, ['claim', 'T-001', '--lease', '0'], /--lease/);
  failsCleanly(db, ['next', '--claim', '--lease', 'soon'], /--lease/);
});

test('next --claim records the worktree and branch, like claim', () => {
  const db = sandbox();
  ok(db, ['add', 'one']);
  const out = ok(db, ['next', '--claim', '--worktree', '/tmp/wt', '--branch', 'fix/x']);
  assert.equal(out.task.worktree, '/tmp/wt');
  assert.equal(out.task.branch, 'fix/x');
});

test('export rejects an unknown --status', () => {
  const db = sandbox();
  ok(db, ['add', 'one']);
  failsCleanly(db, ['export', '--status', 'bogus'], /--status/);
});

test('export --status filters to that status', () => {
  const db = sandbox();
  ok(db, ['add', 'one']);
  ok(db, ['add', 'two']);
  ok(db, ['cancel', 'T-002']);
  const md = run(db, ['export', '--status', 'cancelled']).stdout;
  assert.match(md, /T-002/);
  assert.doesNotMatch(md, /T-001/);
});
