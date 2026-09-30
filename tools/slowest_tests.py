#!/usr/bin/env python3
"""Rank the slowest tests of a `dotnet test` run, from its trx files.

    python3 tools/slowest_tests.py TestResults/*.trx
    python3 tools/slowest_tests.py TestResults/*.trx --top 40
    python3 tools/slowest_tests.py TestResults/*.trx --json

"Which tests ate the wall clock" is a question a trx file already answers, and
answering it by hand is where the mistakes are: the `duration` attribute of a
`UnitTestResult` is an `hh:mm:ss.fffffff` string, so `float(duration)` throws
on precisely the entries the question is about, and an agent that gives up on
the exception concludes the suite has no slow tests. This reads every
`UnitTestResult` out of every file it is given, parses the duration, and prints
them slowest first, with a per-class aggregate underneath (a class whose
methods are each cheap but which pays a per-test setup is invisible in a list
of method names, and visible in a per-class total).

**It reads trx files; it does not run anything.** The run is
`eng/verify.sh` or `dotnet test --logger trx`, and this is the reader for what
that produced. That separation is the point: a lane already writes the file,
and a tool that re-ran the suite would double the most expensive thing in the
repository.

**A lane's trx file is per test project**, so several files in one run is the
normal case and they are merged and ranked together rather than reported
separately: the question is "what was slow in this run", not "what was slow in
each of the eight projects, eight times over".
"""
import argparse
import json
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

#: The trx schema, and the element that carries one result inside it.
TRX = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
RESULT = f"{TRX}UnitTestResult"


@dataclass(frozen=True)
class Slowest:
    """One test result, its wall-clock duration and how it ended."""

    test: str
    outcome: str
    seconds: float

    @property
    def klass(self) -> str:
        """The class, without the assembly or namespace prefix.

        xunit's theory names carry their arguments (`Mensuration_operations_are
        _rejected_by_name(operation: "queryBoundary")`), so the class is what
        is left of the name once the method and the arguments are dropped.
        """
        method = self.test.split("(")[0].rsplit(".", 1)[-1]
        parts = self.test.split("(")[0].split(".")
        return parts[-2] if len(parts) > 1 and parts[-1] == method else ".".join(parts)


def seconds(duration: str | None) -> float:
    """A trx `duration` as seconds.

    The logger writes `hh:mm:ss.fffffff`, and the hours field is present even
    when it is zero — `00:01:56.1230000` is a two-minute test, not a parse
    error. An absent or empty attribute is a result the runner did not time,
    which is zero seconds and not a reason to drop the result: a suite that
    timed nothing has to be visible as untimed rather than absent.
    """
    if not duration:
        return 0.0
    total = 0.0
    for part in duration.strip().split(":"):
        total = total * 60 + float(part)
    return total


def read(path: Path) -> list[Slowest]:
    """Every `UnitTestResult` in one trx file, or a failure naming the file.

    A file that is not a trx is an error rather than an empty list: an empty
    table reads like a suite in which nothing was slow, which is the one
    conclusion this tool must never draw silently. That includes the HTML error
    page a lane leaves behind when the run dies, which parses as XML quite
    happily — so the root element is checked for the trx namespace as well as
    the document being well-formed.
    """
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as exception:
        raise SystemExit(f"{path}: not a trx file ({exception})")
    if root.tag != f"{TRX}TestRun":
        raise SystemExit(f"{path}: not a trx file (root element is {root.tag})")
    return [
        Slowest(result.get("testName") or "<unnamed>", result.get("outcome") or "Unknown",
                seconds(result.get("duration")))
        for result in root.iter(RESULT)
    ]


def clock(value: float) -> str:
    """A duration as `m:ss.mm`, which is how a slow test is worth naming."""
    return f"{int(value // 60)}:{value % 60:05.2f}"


def render(results: list[Slowest], top: int, as_json: bool) -> str:
    """The ranked table, the per-class aggregate, and the summary line."""
    ordered = sorted(results, key=lambda result: (-result.seconds, result.test))
    slowest = ordered[:top]
    total = sum(result.seconds for result in ordered)

    if as_json:
        return json.dumps({
            "results": len(ordered),
            "totalSeconds": total,
            "top": top,
            "slowest": [
                {"test": result.test, "class": result.klass, "outcome": result.outcome,
                 "seconds": result.seconds}
                for result in slowest
            ],
        }, indent=2)

    lines = [f"slowest {len(slowest)} of {len(ordered)} results, {clock(total)} summed"]
    lines += [
        f"  {clock(result.seconds):>9}  {result.outcome:<8} {result.test}"
        for result in slowest
    ]
    aggregate: dict[str, list[float]] = {}
    for result in ordered:
        aggregate.setdefault(result.klass, [0.0, 0.0])
        aggregate[result.klass][0] += result.seconds
        aggregate[result.klass][1] += 1
    lines.append("per class:")
    lines += [
        f"  {clock(span[0]):>9}  n={int(span[1]):<4} {name}"
        for name, span in sorted(aggregate.items(), key=lambda item: -item[1][0])
    ]
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("trx", nargs="+", type=Path, help="trx file(s) to read")
    parser.add_argument("--top", type=int, default=20,
                        help="how many of the slowest to print (default: 20)")
    parser.add_argument("--json", action="store_true", help="machine-readable output")
    arguments = parser.parse_args(argv)

    results: list[Slowest] = []
    for path in arguments.trx:
        results += read(path)
    print(render(results, max(arguments.top, 0), arguments.json))
    return 0


if __name__ == "__main__":
    sys.exit(main())
