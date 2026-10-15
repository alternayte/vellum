#!/usr/bin/env bash
# A released migration never changes. A database of a released version upgrades by the
# migrations added after it, so each migration file that the last release tag (vX.Y.Z, no
# pre-release) holds must still exist, byte for byte. Only the model snapshot changes.
# With no release tag in the clone, the check passes. CI checks out the tags for it.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

tag="$(git tag -l 'v[0-9]*.[0-9]*.[0-9]*' --sort=-v:refname | grep -v -- - | head -n 1 || true)"
if [ -z "$tag" ]; then
    echo "migrations-frozen: no release tag, nothing to check"
    exit 0
fi

changed="$(git diff --name-status "$tag" -- src/Vellum/Migrations \
    | awk '$1 != "A" && $NF !~ /AppDbContextModelSnapshot\.cs$/ { print }')"
if [ -n "$changed" ]; then
    echo "migrations-frozen: these migrations of $tag were changed or deleted. Add a new migration (just migration <name>) instead:" >&2
    echo "$changed" >&2
    exit 1
fi
echo "migrations-frozen: migrations of $tag unchanged"
