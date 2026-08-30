#!/usr/bin/env bash
# Build the whole solution.
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet build SpatialEngine.slnx "$@"