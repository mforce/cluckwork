# AGENTS.md: `.github/`

Rules for CI workflows, releases and deployment instructions. The root [`AGENTS.md`](../AGENTS.md) applies too. [`docs/releasing.md`](../docs/releasing.md) is the how-to; [`351-releases.md`](../docs/decisions/351-releases.md) explains the mechanism.

## Workflow rules

- Pin third-party Actions to a full commit SHA with a trailing `# vX.Y.Z` comment, never a tag. `actions/*` and `github/*` may keep major-version tags.
- `runs-on` names a specific runner label (`ubuntu-26.04`), never `ubuntu-latest`.
- The docs-only gate in `ci.yml` (`changes` job) runs only on `pull_request`. It must never skip a job on `push` or `workflow_dispatch`, because `publish` needs every job and a skipped `image` leaves a merge with no image. An unreadable, empty or failed diff runs every job: use `!cancelled() && ... != 'true'`, not `== 'false'`. `specs/` and `graphify-out/` count as code. Keep `--no-renames`. → [`782-ci-job-gating.md`](../docs/decisions/782-ci-job-gating.md)
- The `tests` matrix keeps `fail-fast: true`. Never add `if:`, `continue-on-error` or `fail-fast: false` to it; no guard catches them. → [`775-ci-test-matrix.md`](../docs/decisions/775-ci-test-matrix.md)
- A new restore input goes into the Dockerfile restore layer, the CI drift guard and every path filter that names `Directory.Packages.props`.

## Releases and image publishing (#351)

- Promotion retags the existing digest server-side. Never rebuild an image to promote it.
- Promotion reads the digest from CI's own run artifact, never by resolving `:sha-<commit>`. Add every release-gating CI job to `publish.needs`.
- The release stays a draft until its image is promoted.
- Every job that mints the GitHub App token keeps `permission-*` downscoping and declares `environment: { name: app-token, deployment: false }`. → [`351-releases.md`](../docs/decisions/351-releases.md#amendment-2026-10-08-the-app-secrets-live-in-the-app-token-environment-1131)
- Hotfix lines are `release/vX.Y.x` branches. Never create one before the *Release lines* and *Release line creation* rulesets exist; a name cut earlier stays untrusted forever. Workflow fixes on `main` reach a hotfix line only by cherry-pick. → [`351-releases.md`](../docs/decisions/351-releases.md#amendment-2026-10-07-hotfix-release-lines-and-one-digit-per-release)
- `Release-As:` counts only when typed at merge time. Never write release-please's commit-override block in a PR body unless you mean to replace the whole commit message.
- Multi-arch publishing builds the index from verified per-arch digests and verifies by digest, never by tag. The arm64 cache key prefix must not begin with amd64's broad `image-layers-` restore key. → [`995-multi-arch-images.md`](../docs/decisions/995-multi-arch-images.md)
- Deploy by the index digest, never by tag or a platform manifest digest. *Obtaining* the digest and *verifying* its origin are separate: get the index reference from the release's `image.json`, verify it with `gh attestation verify` using `--bundle-from-oci`, `--signer-workflow` and `--source-ref` (`refs/heads/main`, or `refs/heads/release/vX.Y.x` for a hotfix), then confirm the version tag still resolves to that digest, comparing against `reference`, never the asset's `digest` field. Full commands: [`docs/releasing.md`](../docs/releasing.md#deploying).

  Net, stated at exactly the strength the argument supports: the internal gate
  fails closed for a leaked **registry** credential. The external gate also
  stops a **branch push** substituting its own bytes. Neither stops a branch
  writer swapping in *other* attested bytes: the tag/digest comparison above
  raises the cost, but that actor holds registry write too, so nothing in this
  repo closes it; branch/dispatch permissions and immutable tags do.
  **And neither survives a merge to `main` or to a hotfix `release/` branch.**
  Once a backdoored `ci.yml` is the definition on either, its attestation is
  genuinely valid (right signer workflow, right source ref), because
  `--source-ref` records *which ref built this*, not *whether that ref's
  content is trustworthy*. This repo allows a self-merge (`main` requires a PR
  but **zero** approving reviews, and the *Release lines* ruleset requires a
  pull request with zero approvals), so that path is open today and no flag on
  the verify command closes it; review of changes to `main` and every release
  branch is the only control that does.

  The paragraph above is the canonical statement of this boundary. `docs/releasing.md` and the `ci.yml` comment summarise it. If you correct it, correct all three.
- Package visibility and the host's pull credential belong to the deployment repo.
