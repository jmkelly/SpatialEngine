#!/usr/bin/env python3
"""Every harness under `eng/` is compiled by a lane, or nothing compiles it.

Run: python3 -m unittest tools/test_spike_harnesses.py

`eng/spike-u2x-query-baseline` and `eng/spike-u2x-tile-cache` are measurement
harnesses that live under `eng/` and are deliberately NOT in
`SpatialEngine.slnx` — that is what keeps them out of the coverage and metrics
gates. The cost of the omission is that *nothing* compiled them: the fast lane
builds a scoped solution derived from `tools/verify_scope.py`, and that graph
starts at the solution's own project list, so a project the solution does not
name is in no lane's build. On 2026-10-02 the query-baseline harness had
stopped compiling entirely — `EsriFilterClause` had become `EsriWhere`,
`IFeatureStore.QueryAsync` took a `FeatureQuery` and answered a
`FeatureQueryPage` — and `SpatialEngine-58d` had to port it by hand before it
could re-measure anything (ADR-0190).

The dated note at the top of `RESULTS.md` was the previous answer and it did
not work: it is written by the person whose harness rotted, and it says nothing
about the *next* change to `Spatial.Core`.

So the rule is a check every lane calls directly, beside
`tools/trailing_whitespace.py` and `tools/conflict_markers.py` (ADR-0143,
ADR-0146): `tools/spike_harnesses.py` derives the ungated harnesses — every
`*.csproj` under `eng/` that the solution does not name — and `dotnet build`s
each. Deriving rather than listing is the point: a harness added tomorrow is
gated without anybody remembering this file. The rules live in
`tools/spike_harnesses.py`, which is what the lanes call (LaneWiringTests
below); one rule, one implementation.
"""
import os
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

# The rules live in `tools/spike_harnesses.py`, which is what the lanes call.
# One rule, one implementation: a test that carries a second copy of it can
# pass while the check every merge runs fails.
from tools.spike_harnesses import ungated_harnesses  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parent.parent
SCRIPT = REPO_ROOT / "tools" / "spike_harnesses.py"
VERIFY_SH = REPO_ROOT / "eng" / "verify.sh"
CI_YML = REPO_ROOT / ".github" / "workflows" / "ci.yml"

#: The two harnesses this record is about. Named here rather than inferred so a
#: rename is a test failure rather than a silently shorter gate.
QUERY_BASELINE = "eng/spike-u2x-query-baseline/Spatial.Spike.QueryBaseline.csproj"
TILE_CACHE = "eng/spike-u2x-tile-cache/Spatial.Spike.TileCache.csproj"

CSPROJ = textwrap.dedent("""\
    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup>
        <OutputType>Exe</OutputType>
        <IsTestProject>false</IsTestProject>
      </PropertyGroup>
    </Project>
""")


def make_tree(root: Path, harnesses=("eng/spike-one/One.csproj",),
              in_solution=("src/Lib/Lib.csproj",), write_solution=True) -> Path:
    """A throwaway tree shaped like this repository: `eng/` beside `src/`."""
    for relative in list(harnesses) + list(in_solution):
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(CSPROJ, encoding="utf-8")
    if write_solution:
        lines = ["<Solution>"]
        lines += [f'  <Project Path="{p}" />' for p in in_solution]
        lines.append("</Solution>")
        (root / "SpatialEngine.slnx").write_text(
            "\n".join(lines) + "\n", encoding="utf-8")
    return root


def fake_dotnet(root: Path, exit_code: int = 0, log: Path | None = None) -> Path:
    """A `dotnet` stand-in that records its arguments and answers a chosen way.

    The build is the one part of the check that needs a toolchain, and a unit
    test that really builds a harness is a test that costs minutes in the
    tooling suite. The shim answers the *contract* — `dotnet build <csproj>`,
    the exit code, the output on failure — and the real build is covered by the
    lane the wiring tests below check for.
    """
    shim = root / "dotnet"
    target = log or (root / "dotnet.log")
    shim.write_text(
        "#!/usr/bin/env bash\n"
        f'printf "%s\\n" "$*" >> "{target}"\n'
        f"echo 'fake build output'\n"
        f"exit {exit_code}\n",
        encoding="utf-8",
    )
    shim.chmod(0o755)
    return shim


def run_check(*arguments, expect=None) -> subprocess.CompletedProcess:
    result = subprocess.run(
        [sys.executable, str(SCRIPT), *arguments],
        cwd=str(REPO_ROOT), capture_output=True, text=True,
    )
    if expect is not None:
        assert result.returncode == expect, result.stdout + result.stderr
    return result


class DiscoveryTests(unittest.TestCase):
    """A harness the solution does not name is what this check is about."""

    def test_a_harness_outside_the_solution_is_found(self):
        with tempfile.TemporaryDirectory() as root:
            tree = make_tree(Path(root))
            self.assertEqual(ungated_harnesses(tree), ["eng/spike-one/One.csproj"])

    def test_a_project_the_solution_names_is_not_built_a_second_time(self):
        # The rule is "no project under `eng/` is left out of any build", not
        # "build everything under `eng/`": a harness moved into the solution is
        # built by the lane that already builds it, and building it here as
        # well would double the cost of the gate for nothing.
        with tempfile.TemporaryDirectory() as root:
            tree = make_tree(
                Path(root),
                harnesses=("eng/spike-one/One.csproj",),
                in_solution=("src/Lib/Lib.csproj", "eng/spike-one/One.csproj"),
            )
            self.assertEqual(ungated_harnesses(tree), [])

    def test_build_output_is_not_a_project(self):
        # `obj/` and `bin/` are regenerated copies: a `csproj` under one of them
        # is not a harness, and reading them would find the copies the previous
        # build left.
        with tempfile.TemporaryDirectory() as root:
            tree = make_tree(Path(root))
            for stray in ("eng/spike-one/obj/One.csproj",
                          "eng/spike-one/bin/Debug/One.csproj"):
                path = tree / stray
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(CSPROJ, encoding="utf-8")
            self.assertEqual(ungated_harnesses(tree), ["eng/spike-one/One.csproj"])

    def test_a_missing_solution_does_not_hide_a_harness(self):
        # The failure this rule exists for is silence, so an unreadable answer
        # is the worst case rather than a reason to build nothing.
        with tempfile.TemporaryDirectory() as root:
            tree = make_tree(Path(root), write_solution=False)
            self.assertEqual(ungated_harnesses(tree), ["eng/spike-one/One.csproj"])

    def test_the_order_is_stable(self):
        # Two harnesses, built in the same order on every machine, so a red
        # build names the same one first.
        with tempfile.TemporaryDirectory() as root:
            tree = make_tree(Path(root), harnesses=(TILE_CACHE, QUERY_BASELINE))
            self.assertEqual(ungated_harnesses(tree), sorted([TILE_CACHE, QUERY_BASELINE]))


class RepositoryTests(unittest.TestCase):
    """The harness this bead is about, in the repository as it stands."""

    def test_both_harnesses_are_outside_the_solution_and_therefore_ungated(self):
        # The reproduction: on 2026-10-02 this harness had stopped compiling,
        # and no lane could see it (SpatialEngine-58d).
        found = ungated_harnesses(REPO_ROOT)
        self.assertEqual(found, sorted([QUERY_BASELINE, TILE_CACHE]),
                         "the harnesses under eng/ are not the pair this gate "
                         "was written for — one of them moved, or a third was "
                         "added and discovery is wrong")

    def test_listing_names_them_without_a_toolchain(self):
        # `--list` is the half that needs no .NET SDK, so the wiring in the
        # tests below can be checked on a box that has none.
        result = run_check("--root", str(REPO_ROOT), "--list", expect=0)
        self.assertIn(QUERY_BASELINE, result.stdout)
        self.assertIn(TILE_CACHE, result.stdout)


class BuildTests(unittest.TestCase):
    """A harness that no longer compiles has to fail the check."""

    def test_each_harness_is_built_with_dotnet_build(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root)
            harness = make_tree(path / "tree", harnesses=("eng/spike-one/One.csproj",))
            log = path / "dotnet.log"
            shim = fake_dotnet(path, log=log)
            run_check("--root", str(harness), "--dotnet", str(shim), expect=0)
            self.assertEqual(
                log.read_text(encoding="utf-8").split(),
                ["build", "eng/spike-one/One.csproj"])

    def test_a_harness_that_does_not_compile_fails_the_check(self):
        # The drift itself, as a build: the check's whole job is to turn "the
        # harness stopped compiling three weeks ago" into a red lane.
        with tempfile.TemporaryDirectory() as root:
            path = Path(root)
            harness = make_tree(path / "tree", harnesses=("eng/spike-one/One.csproj",))
            shim = fake_dotnet(path, exit_code=1)
            result = run_check("--root", str(harness), "--dotnet", str(shim), expect=1)
            self.assertIn("eng/spike-one/One.csproj", result.stdout + result.stderr)
            # The compiler's own output is what says *why*, so it is passed on
            # rather than swallowed.
            self.assertIn("fake build output", result.stdout + result.stderr)

    def test_a_project_the_solution_names_is_not_built(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root)
            harness = make_tree(path / "tree",
                                harnesses=("eng/spike-one/One.csproj",),
                                in_solution=("src/Lib/Lib.csproj",
                                             "eng/spike-one/One.csproj"))
            log = path / "dotnet.log"
            shim = fake_dotnet(path, log=log)
            result = run_check("--root", str(harness), "--dotnet", str(shim), expect=0)
            self.assertFalse(log.exists(), "a project the solution builds was built again")
            self.assertIn("0", result.stdout)

    def test_a_dotnet_that_is_not_there_is_a_failure_not_a_pass(self):
        # A check that cannot run must not read as a clean gate: that is the
        # whole class of false green this record exists to end.
        with tempfile.TemporaryDirectory() as root:
            path = Path(root)
            harness = make_tree(path / "tree", harnesses=("eng/spike-one/One.csproj",))
            result = run_check("--root", str(harness),
                               "--dotnet", str(path / "no-such-dotnet"), expect=1)
            self.assertIn("no-such-dotnet", result.stdout + result.stderr)

    def test_a_tree_with_no_harness_is_a_pass(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root)
            harness = make_tree(path / "tree", harnesses=(),
                                in_solution=("src/Lib/Lib.csproj",))
            result = run_check("--root", str(harness), expect=0)
            self.assertIn("0", result.stdout)


class LaneWiringTests(unittest.TestCase):
    """The check has to be read by the lanes that gate a merge.

    The shape ADR-0143, ADR-0146 and ADR-0148 give the other repository checks.
    A rule that only runs when `tools/**` changed is not a gate, and the change
    that breaks a harness is a `src` change.
    """

    def lanes(self):
        """`eng/verify.sh` split into the text of each lane's block."""
        text = VERIFY_SH.read_text(encoding="utf-8")
        markers = ["# --- the format lane",
                   "# --- the full lane",
                   "# --- the default lane"]
        self.assertTrue(all(m in text for m in markers),
                        "eng/verify.sh no longer carries the lane markers these tests split on")
        blocks = {}
        for index, marker in enumerate(markers):
            end = markers[index + 1] if index + 1 < len(markers) else len(text)
            blocks[marker] = text[text.index(marker):text.index(end) if end != len(text) else len(text)]
        return blocks

    def test_every_verify_lane_runs_the_check(self):
        for marker, block in self.lanes().items():
            self.assertIn("spike_harness_step", block,
                          f"the lane at {marker!r} does not build the harnesses under eng/")

    def test_the_helper_runs_the_check(self):
        text = VERIFY_SH.read_text(encoding="utf-8")
        start = text.index("spike_harness_step() {")
        body = text[start:text.index("\n}", start)]
        self.assertIn("tools/spike_harnesses.py", body)

    def test_the_fast_lane_does_not_gate_it_on_tools_changing(self):
        text = VERIFY_SH.read_text(encoding="utf-8")
        default = self.lanes()["# --- the default lane"]
        conditional = default.index('if [[ "$RUN_TOOLING" == "1" ]]')
        self.assertIn("spike_harness_step", default[:conditional],
                      "the harness build has fallen inside the tools/**-only tooling gate")
        self.assertNotIn("spike_harness_step", default[conditional:])

    def test_ci_runs_the_check(self):
        # The CI `verify` job spells its steps out rather than calling the
        # script, so wiring the lanes alone would leave the job that follows
        # every merge with the same hole.
        self.assertIn("tools/spike_harnesses.py", CI_YML.read_text(encoding="utf-8"))

    def test_a_plan_names_the_step(self):
        # `--plan` prints what a lane would run and runs nothing, so this is
        # the cheapest read of the merge gate's own scope.
        result = subprocess.run(
            ["bash", "eng/verify.sh", "--plan"],
            cwd=str(REPO_ROOT), capture_output=True, text=True,
            env={**os.environ, "VERIFY_NO_QUEUE": "1"},
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("tools/spike_harnesses.py", result.stdout)


if __name__ == "__main__":
    unittest.main()
