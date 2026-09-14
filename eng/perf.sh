#!/usr/bin/env bash
# Performance smoke for PRs: one short BenchmarkDotNet job, filtered to the
# placeholder bench. Must stay green in under 2 minutes. Full micros,
# throughput and nightly suites are follow-ons (T-076/77/78).
set -euo pipefail
cd "$(dirname "$0")/.."

usage() {
  echo "Usage: eng/perf.sh [--smoke]"
  echo "  --smoke   Single short job, placeholder bench only (default)."
}

MODE="--smoke"
for arg in "$@"; do
  case "$arg" in
    --smoke) MODE="--smoke" ;;
    -h | --help) usage; exit 0 ;;
    *) echo "Unknown argument: $arg" >&2; usage >&2; exit 2 ;;
  esac
done

if [[ "$MODE" == "--smoke" ]]; then
  # Artifacts go under artifacts/ (gitignored) so smoke runs never pollute
  # the checkout.
  dotnet run -c Release --project tests/performance/Spatial.Performance/Spatial.Performance.csproj -- \
    --filter "*EnvelopeBenchmarks*" --job short --artifacts artifacts/benchmarks
fi
