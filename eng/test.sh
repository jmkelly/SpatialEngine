#!/usr/bin/env bash
# Run every test suite (unit, architecture, integration).
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet test SpatialEngine.slnx "$@"