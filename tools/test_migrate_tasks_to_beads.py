#!/usr/bin/env python3
"""Tests for tools/migrate-tasks-to-beads.py.

Run: python3 -m unittest tools/test_migrate_tasks_to_beads.py

The migration script is kept in-tree as the reviewable record of how the old
spatial-tasks queue maps onto beads, so the mapping is pinned here.
"""
import importlib.util
import json
import sqlite3
import subprocess
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).with_name("migrate-tasks-to-beads.py")

TASK_COLUMNS = """
    id TEXT PRIMARY KEY, seq INTEGER NOT NULL, title TEXT NOT NULL,
    body TEXT NOT NULL DEFAULT '', area TEXT NOT NULL DEFAULT '',
    priority INTEGER NOT NULL DEFAULT 3, status TEXT NOT NULL DEFAULT 'ready',
    agent TEXT, lease_until TEXT, worktree TEXT, branch TEXT, commit_sha TEXT,
    pr TEXT, blocked_on TEXT, note TEXT,
    created TEXT NOT NULL, updated TEXT NOT NULL,
    claimed_at TEXT, finished_at TEXT
"""


def load_script():
    """Import the dashed script filename as a module."""
    spec = importlib.util.spec_from_file_location(
        "migrate_tasks_to_beads", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def git(*args, cwd):
    return subprocess.run(
        ["git", *args], cwd=cwd, check=True, capture_output=True, text=True)


class DefaultDbPathTests(unittest.TestCase):
    """The default queue path must follow the repo, not a hardcoded clone."""

    def test_uses_git_common_dir(self):
        with tempfile.TemporaryDirectory() as tmp:
            repo = Path(tmp, "repo")
            repo.mkdir()
            git("init", "-q", cwd=repo)
            self.assertEqual(
                load_script().default_db_path(cwd=repo),
                repo.resolve() / ".git" / "spatial-tasks" / "tasks.db")

    def test_worktree_resolves_to_shared_queue(self):
        # AGENTS.md: the DB lives in the git common dir so every worktree
        # sees one queue. A worktree's own .git is a file, not a directory.
        with tempfile.TemporaryDirectory() as tmp:
            main_repo = Path(tmp, "repo")
            main_repo.mkdir()
            git("init", "-q", cwd=main_repo)
            (main_repo / "f.txt").write_text("x")
            git("add", "-A", cwd=main_repo)
            git("-c", "user.email=t@e", "-c", "user.name=t",
                "commit", "-qm", "init", cwd=main_repo)
            worktree = Path(tmp, "wt")
            git("worktree", "add", "-q", "-b", "wt", str(worktree),
                cwd=main_repo)

            resolved = load_script().default_db_path(cwd=worktree)
            self.assertEqual(
                resolved,
                main_repo.resolve() / ".git" / "spatial-tasks" / "tasks.db")
            self.assertNotIn("wt", str(resolved))


class MappingTests(unittest.TestCase):
    """Status and priority mapping, plus a full end-to-end conversion."""

    def make_queue(self, path, rows, deps=()):
        conn = sqlite3.connect(path)
        conn.execute(f"create table tasks ({TASK_COLUMNS})")
        conn.execute(
            "create table deps (task_id TEXT NOT NULL, dep_id TEXT NOT NULL,"
            " primary key (task_id, dep_id))")
        for row in rows:
            keys = ", ".join(row)
            marks = ", ".join("?" for _ in row)
            conn.execute(f"insert into tasks ({keys}) values ({marks})",
                         tuple(row.values()))
        for task_id, dep_id in deps:
            conn.execute("insert into deps values (?, ?)", (task_id, dep_id))
        conn.commit()
        conn.close()

    def row(self, **over):
        base = {
            "id": "T-001", "seq": 1, "title": "t", "body": "b", "area": "",
            "priority": 3, "status": "ready", "agent": None,
            "lease_until": None, "worktree": None, "branch": None,
            "commit_sha": None, "pr": None, "blocked_on": None, "note": None,
            "created": "2026-01-01T00:00:00Z", "updated": "2026-01-01T00:00:00Z",
            "claimed_at": None, "finished_at": None,
        }
        base.update(over)
        return base

    def convert(self, rows, deps=()):
        with tempfile.TemporaryDirectory() as tmp:
            db = Path(tmp, "tasks.db")
            out = Path(tmp, "out.jsonl")
            self.make_queue(db, rows, deps)
            load_script().main([str(db), str(out)])
            return [json.loads(line) for line in
                    out.read_text().splitlines() if line.strip()]

    def test_status_mapping(self):
        for old, new in [("ready", "open"), ("claimed", "in_progress"),
                         ("review", "open"), ("blocked", "blocked"),
                         ("done", "closed"), ("failed", "closed"),
                         ("cancelled", "closed")]:
            with self.subTest(status=old):
                got = self.convert([self.row(id="T-1", status=old)])
                self.assertEqual(got[0]["status"], new)

    def test_priority_inversion(self):
        # spatial-tasks 1..5 with 1 most urgent; beads 0..4 with 0 critical.
        for old, new in [(0, 0), (1, 0), (2, 1), (3, 2), (4, 3), (5, 4)]:
            with self.subTest(priority=old):
                got = self.convert([self.row(id="T-1", priority=old)])
                self.assertEqual(got[0]["priority"], new)

    def test_review_gets_needs_merge_label(self):
        got = self.convert([self.row(id="T-1", status="review", area="host")])
        self.assertIn("needs-merge", got[0]["labels"])
        self.assertIn("host", got[0]["labels"])

    def test_closed_carries_close_reason(self):
        got = self.convert([
            self.row(id="T-1", status="done", finished_at="2026-02-02T10:00:00Z")])
        self.assertEqual(got[0]["close_reason"], "done")
        self.assertEqual(got[0]["closed_at"], "2026-02-02T10:00:00Z")

    def test_provenance_lands_in_notes(self):
        got = self.convert([self.row(
            id="T-1", agent="pi-1", branch="task/x", commit_sha="abc123",
            pr="42")])
        notes = got[0]["description"]
        for expected in ("agent: pi-1", "branch: task/x", "commit: abc123",
                         "pr: #42"):
            self.assertIn(expected, notes)

    def test_dependencies_preserved_and_unknown_dropped(self):
        got = self.convert(
            [self.row(id="T-1"), self.row(id="T-2", seq=2)],
            deps=[("T-1", "T-2"), ("T-1", "T-999")])
        deps = got[0].get("dependencies", [])
        self.assertEqual([d["depends_on_id"] for d in deps], ["T-2"])

    def test_lease_preserved_for_claimed(self):
        got = self.convert([self.row(
            id="T-1", status="claimed", agent="pi-1",
            lease_until="2026-03-01T00:00:00Z")])
        self.assertEqual(got[0]["lease_expires_at"], "2026-03-01T00:00:00Z")
        self.assertEqual(got[0]["assignee"], "pi-1")


if __name__ == "__main__":
    unittest.main()
