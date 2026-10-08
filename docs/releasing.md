# Releasing & container images

Releasing has two stages: **CI publishes an image for every merge; you decide when
those become a version.**

> This file is the **how-to**. The **invariants** — what not to break, and why
> each step is shaped the way it is — live in the release section of
> [`.github/AGENTS.md`](../.github/AGENTS.md#releases-and-image-publishing-351); the full internal
> mechanism (promotion, the release-please split, the App token, the commit-body
> parser) is in [`docs/decisions/351-releases.md`](decisions/351-releases.md).

## 1. Merging a PR into `main`

CI builds amd64 and arm64 images on native runners. Each build boots against a throwaway database before CI publishes one two-platform index
under the commit it came from. CI runs no vulnerability scan; a weekly scan of the
published image (`image-scan.yml`) reports afterwards, so a published image is
built and boot-tested, not scanned:

```
ghcr.io/mforce/cluckwork:sha-<commit>
```

That image is deployable immediately. It just doesn't have a version yet.

Meanwhile a bot keeps a **"Release vX.Y.Z" pull request** up to date, accumulating a
`CHANGELOG.md` from the commits since the last release and working out the next
version number.

## 2. Merging the Release PR

That's the release. It drafts a GitHub release with the changelog, **promotes** the
already-published image to a version, and — only once that succeeds — publishes the
release and creates the tag:

```
ghcr.io/mforce/cluckwork:v0.4.0          # same image, now with a version
ghcr.io/mforce/cluckwork@sha256:…        # the index digest — what you deploy
```

Promotion adds a name to an image that already exists in the registry. Nothing is
rebuilt, so the bytes carrying `v0.4.0` are provably the bytes that passed CI.

## What decides the version

**The branch you release from, not the commit types.** Each release bumps one
digit, once, however many PRs it collects:

| Released from | Every release bumps | Example |
|---|---|---|
| `main` | the **middle** digit | `v1.0.0` → `v1.1.0` → `v1.2.0` |
| a hotfix line, `release/vX.Y.x` | the **last** digit | `v1.1.0` → `v1.1.1` |

`release-please-config.json` sets this: `"versioning": "always-bump-minor"` on
`main`, and `"always-bump-patch"` in each hotfix line's own copy (see
[Hotfix releases](#hotfix-releases)). **The first digit moves only by hand**,
with `Release-As: 2.0.0` added at merge time (see
[Forcing a version](#forcing-a-version)). A `!` or a breaking change moves no
digit. PR #1130, which introduced this scheme, merges with
`--body "Release-As: 1.0.0"`, so `main`'s next release is `v1.0.0`.

Commit types still choose the **changelog section**. The **PR title** is always
the squashed commit subject, so it is the changelog line. `feat!:` marks a
breaking change in the changelog and changes no digit. `chore`/`ci`/`test`/
`style` are hidden from the changelog *text*, and a changelog with no entries
opens no release PR. So merges of only hidden types wait; the release PR
appears with the first visible entry (`feat`, `fix`, `perf`, `refactor`,
`docs`), and the branch's strategy then chooses the version. A
`BREAKING CHANGE:` footer does nothing here, because the squash commit has no
body (see [Forcing a version](#forcing-a-version)).

That is not as noisy as it sounds, because the bump lands in the **pending
release PR**, not in a release. Merges accumulate into one proposed version, and
nothing is released until you merge that PR.

Commit-message rules — including the parser trap that silently drops a whole
commit from the changelog — are in
[`CONTRIBUTING.md`](../CONTRIBUTING.md#commit-messages).

### Forcing a version

Use this for the first digit (`Release-As: 2.0.0`), or to answer a Release
workflow error. This repository squash-merges with the PR title as the commit
subject and an empty commit body. A plain `Release-As:` footer written in a
branch commit or in the PR description is therefore dropped and does nothing.
Add it when you merge:

```bash
gh pr merge <N> -R mforce/cluckwork --squash --body "Release-As: X.Y.Z"
```

or type `Release-As: X.Y.Z` into the extended description box of the
squash-merge dialog. If no PR is waiting to merge, open one holding a single
empty `chore:` commit (`git commit --allow-empty`) and merge it that way. A
hidden `chore:` commit alone opens no release PR, but one carrying
`Release-As` does: the changelog writer keeps a hidden commit with that footer.

Merge-time text is the recommended route, not the only one. release-please
replaces a squash commit's message with a `BEGIN_COMMIT_OVERRIDE` ...
`END_COMMIT_OVERRIDE` block from the PR description, so a description can
still set `Release-As` that way. Check the version every release PR proposes
before merging it; its title states it.

## Deploying

**Deploy by the index digest, never by tag.** Tags can be moved; a digest cannot.
Docker selects the amd64 or arm64 manifest from that index on the target host.
The image attestation names the index digest, so pass the release's full index
reference to `gh attestation verify`, even when deploying on arm64. A platform
manifest digest identifies only one child and is not the subject this workflow
attests.

Two steps, answering two different questions — *which* image, and whether it is
really ours:

```bash
# 0. gh needs registry credentials for an oci:// subject. The token needs
#    read access to the package (`read:packages` on a PAT); GHCR authenticates
#    the token and ignores the username, so any username value works.
echo "$GITHUB_TOKEN" | docker login ghcr.io -u x-access-token --password-stdin

# 1. Obtain the index digest (machine-readable; no prose to parse)
gh release download vX.Y.Z -p image.json -R mforce/cluckwork
REF=$(jq -r .reference image.json)

# 2. Verify those bytes came from this repo's CI
gh attestation verify "oci://$REF" \
  --repo mforce/cluckwork \
  --signer-workflow mforce/cluckwork/.github/workflows/ci.yml \
  --source-ref refs/heads/main \
  --bundle-from-oci
```

All three flags matter, none is the default, and they do **different** jobs:
`--bundle-from-oci` reads the attestation from the registry copy rather than the
GitHub API; `--signer-workflow` binds the claim to this workflow, not merely to
this repo; `--source-ref` binds it to `main`. Only the last two narrow *whose*
claim is accepted. Copy the command as-is, except for a hotfix version, where
the source ref is its release branch (see [Hotfix releases](#hotfix-releases)).

Step 2 is the one that matters, and step 1 cannot substitute for it. Knowing a
digest tells you *what* you are deploying but nothing about *where it came from*
— if someone pushed those bytes by hand, the digest is still a perfectly valid,
perfectly immutable digest. The attestation is a signed claim by this repo's CI
workflow, so bytes **CI never built** carry no such claim and fail the check.
Bytes CI *did* build still verify whoever pushed them, so this does not stop an
*older* attested image being substituted — which is what step 3 is for.

That covers a credential that can push to the registry, and stops a branch
writer getting *their own* bytes deployed. It proves **origin, not currency**,
though — "did CI on `main` (or the hotfix branch) build these bytes", not "are
these the bytes this release promoted" — so also confirm the tag still agrees
with what you verified:

```bash
# 3. Confirm the release's tag still resolves to the digest you just verified.
#    Compare against $REF, NOT against `jq -r .digest`: `image`, `digest` and
#    `reference` are independent fields of one attacker-writable file, so a
#    rewritten asset can point `.reference` at an old digest while leaving
#    `.digest` matching the tag — and a check reading `.digest` would pass while
#    you deploy the old one. $REF is what step 2 verified and what you deploy.
TAGGED=$(docker buildx imagetools inspect ghcr.io/mforce/cluckwork:vX.Y.Z \
  --format '{{json .Manifest.Digest}}' | tr -d '"')
[ "$TAGGED" = "${REF##*@}" ] || exit 1
```

Step 3 catches an asset rewritten on its own. It does **not** catch someone who
can also push to the registry and move the tag to match, and it says nothing
about a change merged to `main` or to a release branch. **Read the deploy bullet in
[`.github/AGENTS.md`](../.github/AGENTS.md#releases-and-image-publishing-351) before relying on
any of this** — it is the canonical statement of what each step does and does not
prove, and of why each flag is required.

The digest also appears at the bottom of the release notes, for humans.
Deployment configuration itself lives in the separate deploy repo, not here.

**Releases cut before this landed support neither step.** Their images were
published before attestation existed, so step 1 404s and step 2 finds nothing to
verify — the digest in the release notes is all there is, and it carries no
proof of origin. This applies to `v0.0.1` only; every release from the next one
on has both.

## Hotfix releases

A hotfix ships a fix on top of an earlier release without releasing everything
merged to `main` since. It goes through a **maintenance branch**,
`release/vX.Y.x`, one per minor line: `release/v1.2.x` carries `v1.2.1`,
`v1.2.2` and so on after `main` released `v1.2.0`. Releases from it take the
same draft, promote and publish path as `main`. The steps below use the one
pre-1.0 line, `release/v0.1.x` from `v0.1.5`.

1. **Create the two release rulesets before the first `release/` branch
   exists.** Both are branch rulesets with enforcement *Active*, targeting
   `release/v*.*.x`:

   - **Release lines.** Rules: *Require a pull request before merging*,
     *Block force pushes* and *Restrict deletions*. The bypass list is empty,
     so admins also change the branch only through a pull request.
   - **Release line creation.** Rule: *Restrict creations*. Only the
     *Repository admin* role is on its bypass list, so an admin can cut the
     branch.

   Promotion reads the branch's active rules (`rules/branches/<branch>`) and
   refuses unless creation, force pushes and direct pushes are all
   restricted. `branches/<branch>`'s `protected` field is not enough, because
   any matching rule sets it. A `release/vX.Y.x` name that existed before the
   rulesets is permanently untrusted as a `--source-ref`. Anyone with push
   access could have run their own `ci.yml` on it and kept images attested to
   that ref, and deleting the branch withdraws none of those attestations. So
   that minor line cannot be hotfixed through this flow; ship the fix from
   `main` instead. No `release/` ref existed when hotfix support was added.
2. **Cut the branch from the release tag.** For a fix on top of `v0.1.5`, as a
   repository admin, the only role on the creation ruleset's bypass list:

   ```bash
   gh api repos/mforce/cluckwork/git/refs -f ref=refs/heads/release/v0.1.x \
     -f sha="$(gh api repos/mforce/cluckwork/commits/v0.1.5 --jq .sha)"
   ```

   A branch runs the `ci.yml` and `release-please.yml` it carries, not
   `main`'s. If the tag already has hotfix support, CI runs on this first push
   but publishes nothing: the commit is the release's own, and `main` already
   published it. If the tag predates hotfix support, as `v0.1.5` does, its
   workflows are main-only and nothing runs on the branch at all until step 3.
3. **Set up the line in its first pull request.** Every line's first PR into
   the branch sets `"versioning": "always-bump-patch"` on the `.` package in
   the branch's `release-please-config.json`. Without it the line proposes the
   next minor, as `main` does, and the Release workflow refuses the proposal.

   If the tag predates hotfix support, the same PR MUST also cherry-pick the
   commit that added it to `main`. The tag predates it when
   `gh api "repos/mforce/cluckwork/contents/.github/scripts/release-line.mjs?ref=v0.1.5"`
   answers 404, as it does for `v0.1.5`:

   ```bash
   git fetch origin main release/v0.1.x
   git switch -c setup-release-line origin/release/v0.1.x
   git cherry-pick -x "$(git log origin/main --diff-filter=A --format=%H \
     -- .github/scripts/release-line.mjs)"
   # then set "versioning": "always-bump-patch" and commit
   ```

   Open it against `release/v0.1.x`. It gets CI because a pull request run
   reads the workflow definitions from the PR's merge commit, which carries
   the new triggers. Merging it is the branch's first push that runs CI and
   publishes an image. If the cherry-pick conflicts, resolve it so the
   branch's `ci.yml` and `release-please.yml` carry the hotfix-line triggers
   (`release/v*.*.x`), the publish gate, and the step that derives the source
   ref, and so `.github/scripts/release-line.mjs` exists. The cherry-picked
   commit also brings `"always-bump-minor"` and a `Release-As: 1.0.0` line in
   its message: set `always-bump-patch` anyway, and squash-merge so that
   message stays off the branch. Do not cherry-pick the fix itself before this
   PR merges: it would get no CI and no image. Titled `chore:`, this PR opens
   no release PR, because hidden types make no changelog entry; the line's
   release PR appears once step 4's `fix:` merges.
4. **Cherry-pick the fix through a pull request.** Branch off
   `release/v0.1.x`, `git cherry-pick -x <sha>` the fix as it landed on `main`,
   and open the PR against `release/v0.1.x`. CI runs on it as on any PR, and
   the PR title is the release note.
5. **Merge the release PR on that branch.** release-please keeps a separate
   "Release v0.1.6" PR whose base is `release/v0.1.x`. Merging it drafts
   `v0.1.6` at that branch's commit, promotes the image the branch's CI
   published, and publishes the release.
6. **Verify the deploy against the branch ref.** In step 2 of
   [Deploying](#deploying), pass `--source-ref refs/heads/release/v0.1.x`.
   Derive it from the version you are deploying (`v0.1.6` belongs to
   `release/v0.1.x`). Do not copy it from `image.json`, which is writable. The
   release notes print the ref promotion verified, for humans. The deploy repo
   (cluckwork-deploy) must accept that ref for a hotfix version; this repo does
   not change it.

**When the Release workflow refuses a proposal.** It checks each release PR's
proposed version before anyone merges it. On a hotfix line it refuses a
version outside that line's `X.Y.x`: the branch's config is not
`always-bump-patch`, or a `Release-As` reached the branch. On any branch it
refuses a version that is already tagged or released. `main` and the hotfix
lines bump different digits, so that happens only when a `Release-As` named a
taken version. Each error names the version to force; force it as in
[Forcing a version](#forcing-a-version).

A **CI repair dispatch** for a hotfix commit must run from its release branch,
for the same reason a repair for a `main` commit must run from `main`.

A release branch keeps its own frozen copies of `ci.yml` and
`release-please.yml`. A later fix to `main`'s workflows, security fixes
included, reaches a live hotfix line only when it is cherry-picked there.

## Notes

- **Pull requests publish nothing.** The publish job only runs on `main` and
  `release/` branches.
- **No version files to edit.** `version.txt` and `.release-please-manifest.json` are
  machine-maintained — editing them by hand desynchronises the bot from reality.
- **The Release PR is built and tested like any other PR.** It's opened with a
  GitHub App token rather than the default Actions token, which matters twice.
  The default token can't open a PR at all unless the repo turns on a setting
  that lifts that restriction for *every* workflow — so instead, opening PRs
  stays behind a credential a job has to ask for by name. And PRs opened by the
  default token get no CI run, so you'd only see a problem after merging rather
  than before. (The released image is verified either way: promotion refuses to
  run unless CI recorded a digest for that commit.)

## When a release goes wrong

- **A release that can't find its image never goes public.** It stays a draft, and
  GitHub doesn't create the tag for a draft — so you get no version pointing at a
  missing image. To finish it: Actions → **Release** → *Run workflow*, with the tag.
- **If a commit was never built at all**, run Actions → **CI** → *Run workflow* with
  that commit's sha first, then the Release step above. This happens when a commit
  message contains `[skip ci]` (GitHub matches it anywhere in the message, so it can
  arrive via a changelog entry) — no run is created, so there is nothing to re-run.
  The dispatch only accepts commits already on the branch it runs from, and
  **must itself be run from `main`** (the default branch in the *Run workflow*
  dropdown), or from the release branch for a hotfix commit. The sha you
  type names the commit to build; the branch you dispatch from decides which
  workflow definition runs, and an image built from a branch dispatch carries
  provenance naming that branch — which the release workflow, and any deploy
  that verifies, both reject.
- **If the `migrate` job fails with SQL state `23514`**, existing rows break a
  constraint the release adds. The migration repaired and recorded nothing, so
  stop the deploy and follow that constraint's remediation rather than editing
  rows to fit. For `CK_AspNetUsers_CredentialEpoch` (#1031), run the preflight
  query in
  [`364-credential-epoch-revocation.md`](decisions/364-credential-epoch-revocation.md#the-stored-epoch-floor-1031)
  before deploying; setting a bad row to 1 revives outstanding credentials.
