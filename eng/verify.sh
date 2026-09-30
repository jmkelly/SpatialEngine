#!/usr/bin/env bash
# Verification, in three lanes. Every one of them is `eng/verify.sh`. The
# contract is ADR-0118 as amended by ADR-0134; these lanes are the
# implementation, ADR-0109.
#
#   eng/verify.sh --fast     the fast lane — the default, and what an agent runs
#   (or: no arguments)       on every iteration *and* what the merge tool runs.
#                            Builds the projects the change reaches (not the
#                            whole solution) and runs the test projects that
#                            reach it, plus Spatial.Architecture.Tests. This is
#                            the MERGE gate (ADR-0134): a few minutes, and the
#                            exhaustive `--full` is an opt-in run rather than
#                            the price of every merge.
#   eng/verify.sh --format   the format lane — `dotnet format
#                            --verify-no-changes` over the projects that own the
#                            changed files (~45 s each, against ~700 s for the
#                            whole solution). Deliberately NOT on the merge
#                            path: formatting is enforced by CI and by an
#                            occasional run, not paid for hundreds of times a
#                            day.
#   eng/verify.sh --full     the full gate: format over the whole solution,
#                            build, every test project, the python tooling
#                            tests. The exhaustive gate: what CI runs, what to
#                            reach for when a change is broad enough to doubt
#                            the scoping, and what `--bead --full` asks the
#                            merge tool for.
#   eng/verify.sh --plan     print the steps a lane would run, and run nothing
#
# The split exists because the flat gate costs ~25 minutes warm on a 12-core
# box and the swarm runs it constantly. Almost all of that is overhead rather
# than signal: `dotnet format` over the solution is ~30% of it, and the 22
# minutes of Spatial.Host.Tests is in there whether or not the branch touched
# the host.
#
# ADR-0134 moved the merge gate off `--full` for the same reason, and paid for
# it with two changes that are cheap and one that is not:
#
#   * the fast lane builds a **scoped solution** — `.verify-scoped.slnx`,
#     generated from the change set — rather than the whole one. MSBuild's cost
#     is loading and evaluating 50 projects, not compiling: measured warm on the
#     swarm's 12-core host, a no-op whole-solution build took 1 m 07 s where the
#     same build scoped to three projects took 8 s;
#   * the test projects run in ONE `dotnet test` invocation over that solution
#     instead of one process per project, which pays the MSBuild load once
#     rather than per project;
#   * what was traded: a merge is now gated on the tests the change reaches,
#     not on every test in the repository. A broken test in an unchanged
#     project reaches main and is caught by CI on `main` (or by the next
#     branch that touches it), not before this merge.
#
# Because the bare name now means the fast lane, CI=true selects --full unless
# a lane is named on the command line: a workflow that calls a bare
# `eng/verify.sh` must not silently become the scoped lane. The merge tool
# names `--fast` for exactly that reason. The split CI jobs are the deliberate
# exception — they spell out the steps they each own.
#
# `--quick` is accepted as a synonym for the fast lane, from the draft of this
# script that had it as a flag.
set -euo pipefail
cd "$(dirname "$0")/.."

LANE=default
LANE_NAMED=0
PLAN_ONLY=0
# `--skip-tests <substring>` (or VERIFY_SKIP_TESTS, comma-separated) drops
# matching test projects from the scoped lane, loudly. It exists for the
# container-backed suites, which cost minutes each on a contended box and are
# covered by CI on `main` whatever a merge does (ADR-0134 §3): a merge that
# skips them is a merge that leaned on CI, and the close reason says so.
SKIP_PATTERNS=()
add_skip_patterns() {
  local list="$1"
  [[ -n "$list" ]] || return 0
  local IFS=','
  for pattern in $list; do
    pattern="$(echo "$pattern" | tr -d '[:space:]')"
    [[ -n "$pattern" ]] && SKIP_PATTERNS+=("$pattern")
  done
}
add_skip_patterns "${VERIFY_SKIP_TESTS:-}"
for arg in "$@"; do
  case "$arg" in
    --full) LANE=full; LANE_NAMED=1 ;;
    --format) LANE=format; LANE_NAMED=1 ;;
    --fast) LANE=default; LANE_NAMED=1 ;;
    --quick) LANE=default; LANE_NAMED=1 ;;
    --skip-tests=*) add_skip_patterns "${arg#--skip-tests=}" ;;
    --plan) PLAN_ONLY=1 ;;
    -h|--help) sed -n '2,45p' "$0"; exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done
while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-tests)
      if [[ $# -lt 2 ]]; then
        echo "--skip-tests needs a substring" >&2
        exit 2
      fi
      add_skip_patterns "$2"
      shift 2
      ;;
    *) shift ;;
  esac
done

# The merge gate is `--fast` and a bare `eng/verify.sh` is that same lane, so
# on a runner an unnamed invocation means the exhaustive gate until a lane says
# otherwise.
# A named lane always wins: the CI jobs that each own half of `--full` depend on
# this, and so does `bd-merge-bead.py`, which names the lane it means so a
# runner's environment cannot decide what a merge costs (ADR-0134).
if [[ "$LANE_NAMED" == "0" && "${CI:-}" == "true" ]]; then
  echo "== CI=true with no lane named: the bare script is the build gate, so the full lane runs =="
  LANE=full
fi

# Run a gate step, or just print it under --plan.
step() {
  if [[ "$PLAN_ONLY" == "1" ]]; then
    echo "$*"
  else
    "$@"
  fi
}

# --- the change set --------------------------------------------------------
#
# tools/verify_scope.py walks `git diff --name-only <base>...HEAD` plus the
# dirty working tree, maps the changed files onto the projects that own them,
# and takes the projects to build (the changed ones plus the test projects that
# reach them) and the test projects to run, over the `ProjectReference` graph.
# When it cannot read the change set — an unresolvable base, a solution-wide
# file such as Directory.Packages.props — it says so, and the lane falls back to
# everything rather than testing nothing. VERIFY_BASE moves the base for a
# measurement or a rebase.
BASE="${VERIFY_BASE:-origin/main}"
SCOPE_ARGS=(--base "$BASE")
EXHAUSTIVE=0
FORMAT_PROJECTS=()
BUILD_PROJECTS=()
TEST_PROJECTS=()
#: Projects a `--skip-tests` pattern may not drop: the structural guard, which
#: is cheap enough to run on every lane and is the one suite that is about the
#: repository rather than the change.
GUARD_PROJECTS=()
RUN_TOOLING=0
while IFS= read -r line; do
  case "$line" in
    format:*) FORMAT_PROJECTS+=("${line#format:}") ;;
    build:*) BUILD_PROJECTS+=("${line#build:}") ;;
    guard:*) GUARD_PROJECTS+=("${line#guard:}") ;;
    tests:*) TEST_PROJECTS+=("${line#tests:}") ;;
    tooling:*) RUN_TOOLING="${line#tooling:}" ;;
    exhaustive:*) EXHAUSTIVE="${line#exhaustive:}" ;;
  esac
done < <(python3 tools/verify_scope.py "${SCOPE_ARGS[@]}" --list plan --machine)

# The skip is applied to the plan, after the scoping, so it is the only thing
# between the change set and what runs — and it is printed, never silent: a lane
# that quietly drops a suite is indistinguishable from one that scoped it out.
if [[ "${#SKIP_PATTERNS[@]}" != "0" ]]; then
  KEPT=()
  DROPPED=()
  for project in "${TEST_PROJECTS[@]}"; do
    skip=0
    for guard in "${GUARD_PROJECTS[@]}"; do
      [[ "$project" == "$guard" ]] && skip=0 && continue 2
    done
    for pattern in "${SKIP_PATTERNS[@]}"; do
      case "$project" in *"$pattern"*) skip=1 ;; esac
    done
    if [[ "$skip" == "1" ]]; then DROPPED+=("$project"); else KEPT+=("$project"); fi
  done
  if [[ "${#DROPPED[@]}" != "0" ]]; then
    echo "== skipped by --skip-tests ${SKIP_PATTERNS[*]} (CI on main covers these) =="
    printf '   %s\n' "${DROPPED[@]}"
    TEST_PROJECTS=("${KEPT[@]}")
    # The scoped solution is the union of the build and test lists, so a suite
    # left in the build list would be built and then handed to `dotnet test`
    # anyway — the skip would be a lie in the plan.
    KEPT_BUILDS=()
    for project in "${BUILD_PROJECTS[@]}"; do
      drop=0
      for skipped in "${DROPPED[@]}"; do
        [[ "$project" == "$skipped" ]] && drop=1
      done
      [[ "$drop" == "1" ]] || KEPT_BUILDS+=("$project")
    done
    BUILD_PROJECTS=("${KEPT_BUILDS[@]}")
  fi
fi

# The scoped solution the fast lane builds and tests through, generated per run
# from the plan above. A stale one left in the repository root is a gate that
# silently keeps building yesterday's change set, so it goes on the way out.
SCOPED_SOLUTION=".verify-scoped.slnx"
trap 'rm -f "$SCOPED_SOLUTION"' EXIT

# The union of what to build and what to test: `dotnet test` ignores the
# non-test projects, so one file serves both steps and neither can drift from
# the plan. A documentation-only change still gets the architecture guard, so
# the test half can be non-empty where the build half is not.
write_scoped_solution() {
  {
    echo "<Solution>"
    local seen=" "
    for project in "${BUILD_PROJECTS[@]}" "${TEST_PROJECTS[@]}"; do
      [[ "$seen" == *" $project "* ]] && continue
      seen="$seen$project "
      echo "  <Project Path=\"$project\" />"
    done
    echo "</Solution>"
  } > "$SCOPED_SOLUTION"
  # Printed, so a run — and `--plan`, which writes and removes it — shows the
  # gate's actual scope instead of the name of a file that is already gone.
  cat "$SCOPED_SOLUTION"
}

# The unscoped answer is the whole solution, so a lane that cannot scope runs
# the whole thing for that step rather than a guess at a narrow one.
scoped_or_all() {
  if [[ "$EXHAUSTIVE" == "1" ]]; then
    echo "SpatialEngine.slnx"
  else
    printf '%s\n' "${FORMAT_PROJECTS[@]:-}"
  fi
}

# --- the format lane -------------------------------------------------------
if [[ "$LANE" == "format" ]]; then
  echo "== format check (scoped to the changed projects) =="
  if [[ "$EXHAUSTIVE" == "1" ]]; then
    echo "the change set is unscoped against $BASE, so this is the whole solution"
  fi
  for project in $(scoped_or_all); do
    [[ -n "$project" ]] || continue
    # Per project, not per solution: the cost is the MSBuild workspace load,
    # and loading 50 projects is what makes the solution-wide run ~700 s.
    step dotnet format "$project" --verify-no-changes
  done
  exit 0
fi

# --- the full lane ---------------------------------------------------------
if [[ "$LANE" == "full" ]]; then
  echo "== format check =="
  # No --no-restore: a clean checkout has no project.assets.json yet, and the
  # format check loads every project through MSBuild before the build step runs.
  step dotnet format SpatialEngine.slnx --verify-no-changes

  echo "== build =="
  step dotnet build SpatialEngine.slnx

  echo "== tests =="
  step dotnet test SpatialEngine.slnx --no-build

  echo "== tooling tests =="
  # The repo also carries Python tooling (tools/). This lane runs its unit
  # tests too, so the exhaustive gate is exhaustive over the tooling as well.
  step python3 -m unittest discover --start-directory tools --pattern 'test_*.py'
  exit 0
fi

# --- the default lane: the fast build gate --------------------------------
echo "== fast build gate (base $BASE) =="

if [[ "$EXHAUSTIVE" == "1" ]]; then
  echo "== the change set is unscoped against $BASE: whole solution, every test =="
  # The build restores implicitly; a separate `dotnet restore` in front of it is
  # the 65 seconds the flat gate never spent on a second pass.
  step dotnet build SpatialEngine.slnx
  step dotnet test SpatialEngine.slnx --no-build
  step python3 -m unittest discover --start-directory tools --pattern 'test_*.py'
  exit 0
fi

if [[ "${#BUILD_PROJECTS[@]}" == "0" ]]; then
  echo "== no project owns a changed file, so there is nothing to compile =="
  write_scoped_solution
else
  echo "== building ${#BUILD_PROJECTS[@]} project(s) the change reaches =="
  write_scoped_solution
  step dotnet build "$SCOPED_SOLUTION"
fi

echo "== tests that reach the change (${#TEST_PROJECTS[@]} project(s)) =="
# One invocation over the scoped solution, not one `dotnet test` process per
# project: the per-process MSBuild load is pure overhead and it is paid once
# here instead of once per suite. Non-test projects in the solution are ignored
# by `dotnet test`, so the build list doubles as the test list.
if [[ "${#TEST_PROJECTS[@]}" != "0" ]]; then
  step dotnet test "$SCOPED_SOLUTION" --no-build
fi

if [[ "$RUN_TOOLING" == "1" ]]; then
  echo "== tooling tests =="
  step python3 -m unittest discover --start-directory tools --pattern 'test_*.py'
else
  echo "== tooling tests skipped: tools/** did not change =="
fi
