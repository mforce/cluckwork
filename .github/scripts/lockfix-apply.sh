#!/usr/bin/env bash
# Trusted applicator for the dependabot-lockfix workflow (#203). Runs in the
# `commit` job (write token present) and executes NO project code — only git and
# file ops. Reads the regenerated locks from $ARTIFACT_DIR, applies them onto the
# PR checkout under `pr/`, and commits+pushes iff ONLY lock files (and the
# Aspire bundle fingerprint) changed.
set -euo pipefail

# The classifier prints the allowlist; keep the 10 paths here in lockstep with
# LOCK_FILES in lockfix.mjs (the classifier is the enforcing check).
LOCKS=(
  "src/Cluckwork.Domain/packages.lock.json"
  "src/Cluckwork.Application/packages.lock.json"
  "src/Cluckwork.Infrastructure/packages.lock.json"
  "src/Cluckwork.Api/packages.lock.json"
  "src/Cluckwork.AppHost/packages.lock.json"
  "src/Cluckwork.Analyzers/packages.lock.json"
  "tests/Cluckwork.Domain.Tests/packages.lock.json"
  "tests/Cluckwork.Application.Tests/packages.lock.json"
  "tests/Cluckwork.Api.IntegrationTests/packages.lock.json"
  "tests/Cluckwork.AppHost.Tests/packages.lock.json"
)

# The Aspire bundle fingerprint, rewritten by compute when an Aspire bump changes
# the version (PINNED_FILES in lockfix.mjs). Unchanged otherwise.
FINGERPRINT=".github/aspire-bundle.fingerprint"

# A lock must parse as JSON; the fingerprint must hold a version line and only
# comment, version and "<os>-<arch> <sha256>" lines.
well_formed() {
  if [ "$1" = "$FINGERPRINT" ]; then
    grep -Eqx 'version [0-9A-Za-z.+-]+' "$2" \
      && ! grep -Evqx '#.*|version [0-9A-Za-z.+-]+|[a-z0-9_]+-[a-z0-9_]+ [0-9a-f]{64}' "$2"
  else
    node -e "JSON.parse(require('fs').readFileSync(process.argv[1],'utf8'))" "$2"
  fi
}

# Apply each artifact file onto its exact allowlisted path, after checking it is
# a regular file in its expected format (reject a poisoned artifact).
root="$(pwd -P)"
FILES=("${LOCKS[@]}")
# A PR branched before the fingerprint existed has none to refresh.
if [ -n "$(git -C pr ls-tree HEAD -- "$FINGERPRINT")" ]; then
  FILES+=("$FINGERPRINT")
fi
for rel in "${FILES[@]}"; do
  src="$ARTIFACT_DIR/$rel"
  [ -f "$src" ] || { echo "missing regenerated file: $rel"; exit 1; }
  well_formed "$rel" "$src" \
    || { echo "regenerated file is not in its expected format: $rel"; exit 1; }
  # The PR tree controls the destination: a symlink on its path would write
  # outside pr/, for example over these trusted scripts.
  [ "$(realpath -m "pr/$rel")" = "$root/pr/$rel" ] \
    || { echo "lock path goes through a symlink: $rel"; exit 1; }
  # ls-tree names the entry itself; ls-files would list a file inside a
  # directory committed at this path.
  [ "$(git -C pr ls-tree --format='%(objectmode) %(path)' HEAD -- "$rel")" = "100644 $rel" ] \
    || { echo "lock is not a tracked regular file: $rel"; exit 1; }
  cp "$src" "pr/$rel"
done

cd pr

# Classify with the TRUSTED classifier (../trusted/...), not any copy on the PR.
# Read each side's own status: under pipefail $? is the last non-zero one, so
# the classifier's 3 or 4 would hide a failed git status, and a classifier that
# exits unread would be blamed for git's SIGPIPE (141).
set +e
git status --porcelain -z | node ../trusted/.github/scripts/lockfix.mjs
read -r status verdict <<<"${PIPESTATUS[*]}"
set -e

case "$status:$verdict" in
  0:3) echo "No lock changes after restore — nothing to do."; exit 0 ;;
  0:4) : ;;  # commit below
  *:[34]) echo "git status failed (exit $status) — not committing."; exit 1 ;;
  *) echo "Classifier refused (exit $verdict) — not committing."; exit 1 ;;
esac

git config user.name "cluckwork-lockfix[bot]"
git config user.email "cluckwork-lockfix[bot]@users.noreply.github.com"
git add -- "${FILES[@]}"
message="chore(deps): regenerate downstream packages.lock.json (#203)"
if ! git diff --cached --quiet -- "$FINGERPRINT"; then
  message="chore(deps): regenerate packages.lock.json and the Aspire bundle fingerprint (#203)"
fi
git commit -m "$message"

# Compare-and-swap: only fast-forward the branch if its tip is still the SHA we
# built on. A moved branch -> non-fast-forward -> rejected (no --force); the next
# CI cycle re-fixes. Never force-push.
git push origin "HEAD:refs/heads/${HEAD_BRANCH}"
