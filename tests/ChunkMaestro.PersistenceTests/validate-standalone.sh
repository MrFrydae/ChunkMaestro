#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
# Use the actual root CPVM, locks, analyzers and solution; do not synthesize weaker policy.
python3 tooling/build/build.py verify
