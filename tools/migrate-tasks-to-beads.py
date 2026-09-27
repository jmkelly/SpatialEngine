#!/usr/bin/env python3
"""One-shot migration: spatial-tasks SQLite queue -> beads JSONL.

Reads the old local queue and emits a JSONL file shaped for `bd import`.
Kept in-tree so the mapping is reviewable and re-runnable; delete once the
beads queue is the only queue.
"""
import json
import sqlite3
import sys
from datetime import datetime, timezone

DB = sys.argv[1] if len(sys.argv) > 1 else \
    "/home/james/Projects/SpatialEngine/.git/spatial-tasks/tasks.db"
OUT = sys.argv[2] if len(sys.argv) > 2 else "/tmp/spatial-tasks.jsonl"

# spatial-tasks: 1..5 with 1 = most urgent.
# beads: 0..4 with 0 = critical, 4 = lowest.
PRIORITY = {0: 0, 1: 0, 2: 1, 3: 2, 4: 3, 5: 4}

# spatial-tasks had a bespoke lifecycle. beads splits ownership (assignee +
# in_progress) from the work state, so:
#   ready    -> open          (nobody owns it)
#   claimed  -> in_progress   (an agent holds the lease)
#   review   -> open          (handed off, not yet merged) + label
#   done     -> closed        (close_reason: verified)
#   failed   -> closed        (close_reason: failed)
#   cancelled-> closed        (close_reason: cancelled)
STATUS = {
    "ready": "open",
    "claimed": "in_progress",
    "review": "open",
    "blocked": "blocked",
    "done": "closed",
    "failed": "closed",
    "cancelled": "closed",
}
CLOSE_REASON = {
    "done": "done",
    "failed": "failed",
    "cancelled": "cancelled",
}


def ts(value):
    """Return a beads-friendly RFC3339 timestamp, or None."""
    if not value:
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00")).astimezone(
            timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    except ValueError:
        return None


def main():
    conn = sqlite3.connect(DB)
    conn.row_factory = sqlite3.Row
    tasks = conn.execute("select * from tasks order by seq").fetchall()

    known = {t["id"] for t in tasks}
    out = []
    skipped_deps = []

    for t in tasks:
        labels = [t["area"]] if t["area"] else []
        if t["status"] == "review":
            labels.append("needs-merge")

        # Anything the old row carried outside the mapped fields goes in notes
        # so nothing is silently dropped on migration.
        facts = []
        if t["agent"]:
            facts.append(f"agent: {t['agent']}")
        if t["branch"]:
            facts.append(f"branch: {t['branch']}")
        if t["worktree"]:
            facts.append(f"worktree: {t['worktree']}")
        if t["commit_sha"]:
            facts.append(f"commit: {t['commit_sha']}")
        if t["pr"]:
            facts.append(f"pr: #{t['pr']}")
        if t["lease_until"]:
            facts.append(f"lease_until: {t['lease_until']}")
        if t["blocked_on"]:
            facts.append(f"blocked_on: {t['blocked_on']}")
        if t["note"]:
            facts.append(f"note: {t['note']}")

        parts = [t["body"].strip()] if t["body"] and t["body"].strip() else []
        if facts:
            parts.append("Migrated from spatial-tasks:\n" + "\n".join(facts))
        description = "\n\n".join(parts)

        issue = {
            "id": t["id"],
            "title": t["title"],
            "description": description,
            "status": STATUS.get(t["status"], "open"),
            "priority": PRIORITY.get(t["priority"], 2),
            "issue_type": "task",
            "labels": labels,
            "source_system": "spatial-tasks",
        }
        if t["agent"]:
            issue["assignee"] = t["agent"]
        if t["created"]:
            issue["created_at"] = ts(t["created"])
        if t["updated"]:
            issue["updated_at"] = ts(t["updated"])
        if t["claimed_at"]:
            issue["started_at"] = ts(t["claimed_at"])
        if t["status"] == "claimed" and t["lease_until"]:
            issue["lease_expires_at"] = ts(t["lease_until"])
        if t["finished_at"]:
            issue["closed_at"] = ts(t["finished_at"])
        if t["status"] in CLOSE_REASON:
            issue["close_reason"] = CLOSE_REASON[t["status"]]
        # beads carries dependencies nested on the issue, not as separate
        # records. Old edges were undirected-typed: task depends on dep.
        deps = []
        for dep in conn.execute(
                "select dep_id from deps where task_id = ?", (t["id"],)):
            dep_id = dep["dep_id"]
            if dep_id not in known:
                skipped_deps.append((t["id"], dep_id))
                continue
            deps.append({
                "issue_id": t["id"],
                "depends_on_id": dep_id,
                "type": "blocks",
                "created_at": ts(t["created"]),
            })
        if deps:
            issue["dependencies"] = deps

        out.append(issue)

    with open(OUT, "w") as fh:
        for rec in out:
            fh.write(json.dumps(rec) + "\n")

    edges = sum(len(r.get("dependencies", ())) for r in out)
    print(f"wrote {len(out)} records to {OUT}")
    print(f"  issues:      {len(out)}")
    print(f"  deps:        {edges}")
    if skipped_deps:
        print(f"  SKIPPED deps pointing at unknown ids: {skipped_deps}")


if __name__ == "__main__":
    main()
