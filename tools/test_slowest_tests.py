#!/usr/bin/env python3
"""Tests for `tools/slowest_tests.py` — which tests of a suite ate the wall clock.

Run: python3 -m unittest tools/test_slowest_tests.py

`SpatialEngine-ou1` had to answer "which of the three candidates is the two
minute in-process request", and the answer needed the slowest 20 tests of a
793-test suite read out of a trx file. Doing that with a throwaway script means
the next agent re-derives the parser, and — worse — re-derives it wrongly: the
`duration` attribute of a `UnitTestResult` is an `hh:mm:ss.fffffff` string, so
`float(duration)` throws on exactly the entries the question is about (the
long ones). This is the parser that survived, so the measurement in ADR-0154 can
be re-run rather than believed.

The classes below cover the duration formats, the ordering and the `--top`
bound, the per-class aggregate, several trx files in one run (a scoped lane
writes one per test project), a result with no `duration` attribute, and a file
that is not a trx at all — which has to fail loudly rather than print an empty
table that reads like a suite with no slow tests.
"""
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
SLOWEST = REPO / "tools" / "slowest_tests.py"

TRX = """<?xml version="1.0" encoding="UTF-8"?>
<TestRun id="1" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
{results}
  </Results>
</TestRun>
"""

RESULT = """    <UnitTestResult executionId="x" testName="{name}" outcome="{outcome}" duration="{duration}" />"""


def trx(results):
    """A trx file as the VSTest trx logger writes one, minimal but real."""
    return TRX.format(results="\n".join(RESULT.format(**r) for r in results))


def run(*args):
    return subprocess.run(
        [sys.executable, str(SLOWEST), *args],
        capture_output=True, text=True, check=False,
    )


class TrxParsingTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)

    def write(self, name, text):
        path = Path(self.directory.name) / name
        path.write_text(text)
        return path

    def test_the_three_duration_formats_the_logger_writes_all_parse(self):
        # "00:00:31.4300000" is what a sub-minute test looks like, and
        # float() throws on it — which is the entry the whole tool exists for.
        path = self.write("a.trx", trx([
            {"name": "Fast", "outcome": "Passed", "duration": "00:00:01.5000000"},
            {"name": "Slow", "outcome": "Passed", "duration": "00:01:56.1230000"},
            {"name": "VerySlow", "outcome": "Failed", "duration": "00:02:12.0000000"},
        ]))

        result = run(str(path))

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertLess(result.stdout.index("VerySlow"), result.stdout.index("Slow"))
        self.assertLess(result.stdout.index("Slow"), result.stdout.index("Fast"))
        self.assertIn("2:12.00", result.stdout)
        self.assertIn("1:56.12", result.stdout)

    def test_a_result_with_no_duration_is_counted_and_not_silently_the_slowest(self):
        path = self.write("a.trx", trx([
            {"name": "NoDuration", "outcome": "Passed", "duration": ""},
            {"name": "Measured", "outcome": "Passed", "duration": "00:00:05.0000000"},
        ]))

        result = run(str(path))

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertLess(result.stdout.index("Measured"), result.stdout.index("NoDuration"))
        self.assertIn("2 results", result.stdout)

    def test_several_trx_files_are_merged_and_ranked_together(self):
        one = self.write("one.trx", trx([
            {"name": "A.Project1.Slow", "outcome": "Passed", "duration": "00:00:30.0000000"},
        ]))
        two = self.write("two.trx", trx([
            {"name": "B.Project2.Slow", "outcome": "Passed", "duration": "00:01:00.0000000"},
        ]))

        result = run(str(one), str(two))

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertLess(result.stdout.index("B.Project2"), result.stdout.index("A.Project1"))
        self.assertIn("2 results", result.stdout)

    def test_a_file_that_is_not_a_trx_fails_loudly(self):
        path = self.write("a.trx", "<html>the lane failed before it wrote results</html>")

        result = run(str(path))

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("a.trx", result.stderr)

    def test_a_trx_with_no_results_is_an_empty_suite_not_a_crash(self):
        path = self.write("a.trx", trx([]))

        result = run(str(path))

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("0 results", result.stdout)


class OutputTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        results = [
            {"name": f"Spatial.Host.Tests.SomeTests.Case_{i}", "outcome": "Passed",
             "duration": f"00:00:{i:02d}.0000000"}
            for i in range(1, 6)
        ]
        self.path = Path(self.directory.name) / "a.trx"
        self.path.write_text(trx(results))

    def test_the_slowest_20_by_default_is_what_the_bead_asked_for(self):
        result = run(str(self.path))

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("slowest 5 of 5", result.stdout)
        for i in range(1, 6):
            self.assertIn(f"Case_{i}", result.stdout)

    def test_top_bounds_the_table_and_is_said_out_loud(self):
        result = run(str(self.path), "--top", "2")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("slowest 2 of 5", result.stdout)
        self.assertIn("Case_5", result.stdout)
        self.assertNotIn("Case_1", result.stdout)

    def test_the_per_class_aggregate_names_the_class_not_just_the_method(self):
        result = run(str(self.path))

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("SomeTests", result.stdout)

    def test_json_is_machine_readable_for_a_measurement_written_into_a_record(self):
        import json

        result = run(str(self.path), "--json")

        self.assertEqual(result.returncode, 0, result.stderr)
        payload = json.loads(result.stdout)
        self.assertEqual(payload["results"], 5)
        self.assertEqual(payload["totalSeconds"], 15.0)
        self.assertEqual(payload["slowest"][0]["test"], "Spatial.Host.Tests.SomeTests.Case_5")
        self.assertEqual(payload["slowest"][0]["seconds"], 5.0)
        self.assertEqual(payload["slowest"][0]["outcome"], "Passed")


if __name__ == "__main__":
    unittest.main()
