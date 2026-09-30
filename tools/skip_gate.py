#!/usr/bin/env python3
"""Fail a test run in which a suite skipped most of what it was asked to run.

    python3 tools/skip_gate.py --results-dir .verify-test-results --expect 4

`dotnet test` exits 0 on a run in which nothing failed, and the container-backed
suites turn "the Docker daemon was not there when I looked" into `Skip.IfNot(
_fixture.DockerAvailable, …)` — a hundred skipped cases and a green exit code
(SpatialEngine-8lj, from the SpatialEngine-u2x.58 merge: 10 passed / 104
skipped, with the two conformance tests that bead existed to fix among the
skipped). A gate that reads the exit code alone cannot tell that from a run
that passed, and the gate is what merges (ADR-0134), so the count is read here
and the run is failed when it is a mass-skip rather than a conditional case.

**What counts as a mass-skip.** A per-suite *ratio*, not a total: `Spatial.
PostGIS.Tests` and `Spatial.Host.Tests` running green while `Spatial.
SqlServer.Tests` skips 91% of itself is contention under parallel worktrees,
which is the case the gate has to catch, and a repository-wide ratio would
dilute it into the thousands of passing tests around it. And a floor of
`VERIFY_MIN_SKIPPED` (10) on top of the ratio, because six conditional cases out
of 790 is a conditional test, not a suite that did not run — a threshold with
no floor turns into noise the swarm learns to route around, which is what
ADR-0118 was written to prevent.

**What does not count.** A suite dropped by `--skip-tests` is absent from the
run, so it is out of `--expect` and never judged here; that opt-out stays the
only way to run a suite absent, and it is recorded in the bead's close reason
(ADR-0134 §3). A suite whose result file went missing is *not* a pass either:
the trx file is stamped to the second, so two projects finishing in the same
second can overwrite one another, and a lost suite is the case the whole gate
exists to stop being invisible.

Both thresholds are readable from the environment — `VERIFY_SKIP_RATIO`,
`VERIFY_MIN_SKIPPED` — so a suite with a genuinely conditional case is moved
over the line loudly rather than by editing the gate.
"""
from __future__ import annotations

import argparse
import os
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

#: The VSTest trx schema, 2010. Every trx file since VSTest 2012 carries it.
TRX_NAMESPACE = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"

#: More than this fraction of a suite's tests skipped is a suite that did not
#: run. Overridable with `VERIFY_SKIP_RATIO`.
DEFAULT_SKIP_RATIO = 0.5

#: ...and at least this many skipped cases, so a handful of conditional cases in
#: a large suite is not a mass-skip. Overridable with `VERIFY_MIN_SKIPPED`.
DEFAULT_MIN_SKIPPED = 10


@dataclass(frozen=True)
class SuiteResult:
    """What one trx file says about the project that wrote it."""

    #: The assembly the results came from, read off the test names — the trx
    #: schema names the machine and the date, not the assembly.
    name: str
    total: int
    passed: int
    failed: int
    skipped: int
    #: Why the file could not be read, when it could not be. Unknown numbers
    #: are a failure, not a shrug.
    unreadable: str = ""

    @property
    def ratio(self) -> float:
        return self.skipped / self.total if self.total else 0.0


def _int(element, attribute: str) -> int:
    """One `Counters` attribute as an int, absent meaning zero."""
    value = element.get(attribute) if element is not None else None
    try:
        return int(value)
    except (TypeError, ValueError):
        return 0


def _assembly_name(root: ET.Element, fallback: str) -> str:
    """The project a trx file describes.

    A test name is `Namespace.Class.Method` and the namespace is the assembly
    in every suite in this repository, so the name is everything before the last
    two segments. A trx with no results at all (a project with no tests) has
    nothing to read, and its file name stands in. The name is a label, not the
    decision: an unrecognisable one is reported as such and the suite is still
    judged on its counts.
    """
    prefixes: dict[str, int] = {}
    for result in root.iter(f"{TRX_NAMESPACE}UnitTestResult"):
        name = result.get("testName", "")
        # A theory's display name carries its arguments — `Method(x: 1, y: 2)` —
        # and those carry dots, so the arguments come off before the name is
        # cut into namespace, class and method. What is left is the namespace,
        # which is the assembly name in every suite in this repository.
        name = name.split("(")[0].strip()
        parts = name.split(".")
        if len(parts) >= 3:
            prefix = ".".join(parts[:-2])
            prefixes[prefix] = prefixes.get(prefix, 0) + 1
    if not prefixes:
        return fallback
    return max(sorted(prefixes), key=lambda prefix: prefixes[prefix])


def read_suite(path: Path) -> SuiteResult:
    """One trx file as a `SuiteResult`, or a suite whose numbers are unknown."""
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as error:
        return SuiteResult(name=path.stem, total=0, passed=0, failed=0,
                           skipped=0, unreadable=str(error))
    counters = next(iter(root.iter(f"{TRX_NAMESPACE}Counters")), None)
    return SuiteResult(
        name=_assembly_name(root, path.stem),
        total=_int(counters, "total"),
        passed=_int(counters, "passed"),
        failed=_int(counters, "failed"),
        # `notExecuted` is what the trx schema calls a skipped case: a test that
        # was discovered, reported NotExecuted, and was never run.
        skipped=_int(counters, "notExecuted"))


def over_threshold(result: SuiteResult, ratio: float, minimum: int) -> bool:
    """Whether one suite's skips are a mass-skip rather than a conditional case."""
    return bool(result.unreadable) or (result.skipped >= minimum
                                       and result.ratio > ratio)


def report(results_dir: Path, expect: int, ratio: float,
           minimum: int) -> tuple[list[SuiteResult], bool]:
    """Print what the run skipped; return the suites over the line, and
    whether fewer suites reported than the run was told to run.

    The second half of the return is a failure in its own right (ADR-0139 §2):
    the ERROR line below is only worth printing if it is also *returned*, or a
    missing suite is a note in a log on a run whose exit code is 0 — and the
    exit code is what merges (SpatialEngine-huv).
    """
    suites = sorted(
        (read_suite(path) for path in sorted(results_dir.glob("*.trx"))
         if path.is_file()),
        key=lambda result: (-result.skipped, result.name))
    over = [result for result in suites
            if over_threshold(result, ratio, minimum)]

    print(f"== skips: {len(suites)} suite(s) reported, "
          f"{len(over)} over the threshold "
          f"(more than {int(ratio * 100)}% and at least {minimum} skipped) ==")
    for result in suites:
        if result.unreadable:
            print(f"   {result.name}: result file unreadable "
                  f"({result.unreadable})   OVER THRESHOLD")
            continue
        share = f"{result.ratio * 100:.0f}%"
        print(f"   {result.name}: {result.total} test(s), {result.passed} passed, "
              f"{result.failed} failed, {result.skipped} skipped ({share})"
              + ("   OVER THRESHOLD" if result in over else ""))
    missing = bool(expect) and len(suites) < expect
    if missing:
        print(f"   ERROR: {len(suites)} of {expect} suite(s) in this run left a "
              f"result file. A trx file is stamped to the second, so a suite's "
              f"numbers can be overwritten by another project's; a suite that is "
              f"missing is not a suite that passed.")
    return over, missing


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--results-dir", type=Path, required=True,
                        help="where `dotnet test --logger trx` wrote the files")
    parser.add_argument("--expect", type=int, default=0,
                        help="how many test projects the run was asked to run")
    parser.add_argument("--max-skip-ratio", type=float,
                        default=float(os.environ.get("VERIFY_SKIP_RATIO",
                                                     DEFAULT_SKIP_RATIO)),
                        help="the fraction of a suite above which skips are a "
                             "mass-skip (default: %(default)s)")
    parser.add_argument("--min-skipped", type=int,
                        default=int(os.environ.get("VERIFY_MIN_SKIPPED",
                                                   DEFAULT_MIN_SKIPPED)),
                        help="the number of skipped cases below which this is a "
                             "conditional test, not a suite that did not run "
                             "(default: %(default)s)")
    args = parser.parse_args(argv)

    if not args.results_dir.is_dir():
        # No trx at all: the run wrote its results somewhere else, or no suite
        # ran. With nothing in scope that is fine; with suites in scope it is
        # the missing-file case, said in one line rather than a count of zero.
        if args.expect:
            print(f"== skips: no result file in {args.results_dir} for "
                  f"{args.expect} suite(s) ==")
            print("   ERROR: a run that left no results did not pass anything.")
            return 1
        print(f"== skips: no result file in {args.results_dir}, and no suite was "
              f"in scope ==")
        return 0

    over, missing = report(args.results_dir, args.expect, args.max_skip_ratio,
                           args.min_skipped)
    for result in over:
        if result.unreadable:
            print(f"   ERROR: {result.name} left a result file this gate cannot "
                  f"read ({result.unreadable}), so nothing about that suite is "
                  f"known and a suite whose numbers are unknown has not passed.")
            continue
        print(f"   ERROR: {result.name} skipped {result.skipped} of "
              f"{result.total} tests ({result.ratio * 100:.0f}%). A mass-skipped "
              f"suite is not a green suite: re-run the affected class standalone "
              f"on an idle box, or leave the suite to CI with "
              f"`--skip-tests {result.name}` and say so in the close reason.")
    # Either half is a red lane. A run of green suites with one that left no
    # result file was the case the gate reported and then merged on anyway
    # (SpatialEngine-huv).
    return 1 if (over or missing) else 0


if __name__ == "__main__":
    sys.exit(main())
