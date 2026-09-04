#!/usr/bin/env bash
# Trusted applicator for the dependabot-lockfix workflow (#203). Runs in the
# `commit` job (write token present) and executes NO project code — only git and
# file ops. Reads the regenerated locks from $ARTIFACT_DIR, applies them onto the
# PR checkout under `pr/`, and commits+pushes iff ONLY lock files changed.
set -euo pipefail

# The classifier prints the allowlist; keep the 9 paths here in lockstep with
# LOCK_FILES in lockfix.mjs (the classifier is the enforcing check).
LOCKS=(
  "src/Cluckwork.Domain/packages.lock.json"
  "src/Cluckwork.Application/packages.lock.json"
  "src/Cluckwork.Infrastructure/packages.lock.json"
  "src/Cluckwork.Api/packages.lock.json"
  "src/Cluckwork.AppHost/packages.lock.json"
  "tests/Cluckwork.Domain.Tests/packages.lock.json"
  "tests/Cluckwork.Application.Tests/packages.lock.json"
  "tests/Cluckwork.Api.IntegrationTests/packages.lock.json"
  "tests/Cluckwork.AppHost.Tests/packages.lock.json"
)

# Apply each artifact file onto its exact allowlisted path, after checking it is
# a regular file and parses as JSON (reject a poisoned artifact).
for rel in "${LOCKS[@]}"; do
  src="$ARTIFACT_DIR/$rel"
  [ -f "$src" ] || { echo "missing regenerated lock: $rel"; exit 1; }
  node -e "JSON.parse(require('fs').readFileSync(process.argv[1],'utf8'))" "$src" \
    || { echo "regenerated lock is not valid JSON: $rel"; exit 1; }
  cp "$src" "pr/$rel"
done

cd pr

# Classify with the TRUSTED classifier (../trusted/...), not any copy on the PR.
set +e
git status --porcelain -z | node ../trusted/.github/scripts/lockfix.mjs
verdict=$?
set -e

case "$verdict" in
  3) echo "No lock changes after restore — nothing to do."; exit 0 ;;
  0) : ;;  # commit below
  *) echo "Classifier refused (exit $verdict) — not committing."; exit 1 ;;
esac

git config user.name "cluckwork-lockfix[bot]"
git config user.email "cluckwork-lockfix[bot]@users.noreply.github.com"
git add -- "${LOCKS[@]}"
git commit -m "chore(deps): regenerate downstream packages.lock.json (#203)"

# Compare-and-swap: only fast-forward the branch if its tip is still the SHA we
# built on. A moved branch -> non-fast-forward -> rejected (no --force); the next
# CI cycle re-fixes. Never force-push.
git push origin "HEAD:refs/heads/${HEAD_BRANCH}"
