#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# Deliberate public test keys are not private material and are excluded from this scan.
if rg -n --hidden \
  -g '!.git/**' \
  -g '!**/bin/**' \
  -g '!**/obj/**' \
  -g '!tests/vectors/golden/**' \
  '(BEGIN (RSA|OPENSSH|EC|DSA|PGP) PRIVATE KEY|AKIA[0-9A-Z]{16}|xox[baprs]-[0-9A-Za-z-]{10,}|github_pat_[A-Za-z0-9_]{20,}|npm_[A-Za-z0-9]{20,})' \
  .; then
  echo "secret scan failed" >&2
  exit 1
fi
