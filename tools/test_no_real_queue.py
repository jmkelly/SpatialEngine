#!/usr/bin/env python3
"""The tooling suite must not be able to reach the real beads queue.

Run: python3 -m unittest tools/test_no_real_queue.py

`tools/bd-safe-reclaim.py` is the AGENTS.md recovery path: the one command in
the repo that hands live leases back to the queue, and one `bd reclaim` away
from dropping eight in-flight beads (SpatialEngine-u2x.30). Its unit tests
drive `main()` with a stubbed `run`, so today nothing reaches the real queue —
but that is a convention every call site has to remember, not a property
anything enforces, and a test that forgot the stub would mutate the repository's
shared `.beads` database with no other trace than a `reclaimed 1, kept 1` line
in the gate's output. That is exactly what the coordinator read on the tick
that merged SpatialEngine-u2x.32 and mistook for a live recovery
(SpatialEngine-k0p).

So the contract is checked from the outside: the whole tooling suite is run
again with `bd` and `paseo` shadowed on `PATH` by stubs that record the call
and fail. Nothing reaching the queue is then a pass of the *suite*, not a
review of every test's call sites, and a future test that forgets the stub
turns this red. The same run also pins the second half of the symptom: the
gate's output must not carry a report that reads like a live recovery, because
that output is what a reader (or a later agent) took as evidence of one.

The guard is skipped inside the nested run it starts, so the suite terminates.
"""
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
TOOLS = Path(__file__).resolve().parent

# Set in the nested run so this test does not recurse into itself; the stubs
# read the same name to record what was called.
GUARD_ENV = "SPATIALENGINE_QUEUE_GUARD"
MARKER_ENV = "SPATIALENGINE_QUEUE_GUARD_MARKER"

# The CLIs whose real invocation would reach the queue or the live agent list.
QUEUE_COMMANDS = ("bd", "paseo")

# Lines the tool emits for a real recovery. Their absence is what tells a
# reader the gate ran fixtures rather than the queue.
REPORT_LINES = ("RECLAIM ", "KEEP    ", "WOULD   ", "reclaimed ", "keep ")


def _stub(directory, name, marker):
    """A `name` on PATH that records the call and fails, like a broken install."""
    path = directory / name
    path.write_text(textwrap.dedent(f"""\
        #!/bin/sh
        echo "{name} $*" >> "{marker}"
        echo "{name}: the tooling suite reached for the real CLI" >&2
        exit 97
        """))
    path.chmod(0o755)


class RealQueueGuardTests(unittest.TestCase):
    """One nested run of the whole tooling suite, two properties on it."""

    result = None
    calls = None

    @classmethod
    def setUpClass(cls):
        if os.environ.get(GUARD_ENV) == "1":
            raise unittest.SkipTest("already running under the queue guard")
        with tempfile.TemporaryDirectory() as raw:
            stubs = Path(raw)
            marker = stubs / "calls.log"
            for name in QUEUE_COMMANDS:
                _stub(stubs, name, marker)
            env = dict(os.environ)
            env["PATH"] = f"{stubs}{os.pathsep}{env['PATH']}"
            env[MARKER_ENV] = str(marker)
            env[GUARD_ENV] = "1"
            cls.result = subprocess.run(
                [sys.executable, "-m", "unittest", "discover",
                 "--start-directory", str(TOOLS), "--pattern", "test_*.py"],
                cwd=str(REPO), env=env, capture_output=True, text=True)
            cls.calls = (marker.read_text(encoding="utf-8")
                         if marker.exists() else "")

    def test_the_tooling_suite_never_calls_bd_or_paseo(self):
        # The property that matters: a test that forgets its stub fails here,
        # rather than reclaiming a real lease on a coordinator tick.
        self.assertEqual(
            self.calls, "",
            "the tooling suite invoked the real queue CLI:\n" + self.calls)

    def test_the_tooling_suite_never_reports_a_live_recovery(self):
        # The symptom as it was observed: a gate whose output read
        # 'RECLAIM SpatialEngine-fhf ... reclaimed 1, kept 1' looked like the
        # recovery path had been run against the repository's real queue.
        output = (self.result.stdout or "") + (self.result.stderr or "")
        reported = [line for line in output.splitlines()
                    if any(word in line for word in REPORT_LINES)
                    and "bd-safe-reclaim" not in line]
        self.assertEqual(
            reported, [],
            "the tooling run emitted a live-looking recovery report:\n"
            + "\n".join(reported))

    def test_the_nested_run_is_green(self):
        # A guard that only ever failed its neighbours would be a red lane on
        # a perfectly good change, so the suite must still pass under it.
        output = (self.result.stdout or "") + (self.result.stderr or "")
        self.assertEqual(self.result.returncode, 0, output[-4000:])
        self.assertIn("OK", output)


if __name__ == "__main__":
    unittest.main()
