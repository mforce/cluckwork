# Releasing & container images

Releasing has two stages: **CI publishes an image for every merge; you decide when
those become a version.**

> This file is the **how-to**. The **invariants** — what not to break, and why
> each step is shaped the way it is — live in the release section of
> [`AGENTS.md`](../AGENTS.md#releases-and-image-publishing-351); the full internal
> mechanism (promotion, the release-please split, the App token, the commit-body
> parser) is in [`docs/decisions/351-releases.md`](decisions/351-releases.md).

## 1. Merging a PR into `main`

CI builds the image, scans it for vulnerabilities, boots it against a throwaway
database — and if all of that passes, publishes it under the commit it came from:

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
ghcr.io/mforce/cluckwork@sha256:…        # the digest — what you deploy
```

Promotion adds a name to an image that already exists in the registry. Nothing is
rebuilt, so the bytes carrying `v0.4.0` are provably the bytes that passed CI.

## What decides the version

Your **PR title** — or, on a **one-commit** branch, that commit's own subject,
which GitHub uses instead. Either way it becomes the squashed commit subject:

While the version is **below 1.0.0**, everything is deliberately damped one level —
the project is pre-1.0 and shouldn't burn major digits on Phase 1.x churn:

| PR title starts with | Effect on `v0.3.2` |
|---|---|
| `feat!:` or a `BREAKING CHANGE` footer | `v0.4.0` |
| `feat:` | `v0.3.3` |
| anything else (`fix:`, `perf:`, `chore:`, `docs:`, `ci:` …) | `v0.3.3` |

So below 1.0.0 only a **breaking change** moves the minor digit; everything else,
features included, is a patch. `chore`/`ci`/`test`/`style` are hidden from the
changelog *text*, but they still move the number.

**Once the version reaches 1.0.0 this changes**, and it changes silently — the two
`*-pre-major` settings in `release-please-config.json` stop applying, so `feat:`
starts bumping the minor digit and a breaking change bumps the major. Getting to
1.0.0 is therefore a deliberate act: bump it with a `Release-As: 1.0.0` footer when
you mean it, not by accident.

That is not as noisy as it sounds, because the bump lands in the **pending release
PR**, not in a release. Several chore merges accumulate into one proposed patch, and
nothing is released until you merge that PR.

Commit-message rules — including the parser trap that silently drops a whole
commit from the changelog — are in
[`CONTRIBUTING.md`](../CONTRIBUTING.md#commit-messages).

## Deploying

**Deploy by digest, never by tag.** Tags can be moved; a digest cannot.

Two steps, answering two different questions — *which* image, and whether it is
really ours:

```bash
# 0. gh needs registry credentials for an oci:// subject. The token needs
#    read access to the package (`read:packages` on a PAT); GHCR authenticates
#    the token and ignores the username, so any username value works.
echo "$GITHUB_TOKEN" | docker login ghcr.io -u x-access-token --password-stdin

# 1. Obtain the digest (machine-readable; no prose to parse)
gh release download vX.Y.Z -p image.json -R mforce/cluckwork
REF=$(jq -r .reference image.json)

# 2. Verify those bytes came from this repo's CI
gh attestation verify "oci://$REF" \
  --repo mforce/cluckwork \
  --signer-workflow mforce/cluckwork/.github/workflows/ci.yml \
  --source-ref refs/heads/main \
  --bundle-from-oci
```

All three flags matter and none is the default — each narrows *whose* claim is
accepted (the registry copy, one workflow, one branch). Copy the command as-is.

Step 2 is the one that matters, and step 1 cannot substitute for it. Knowing a
digest tells you *what* you are deploying but nothing about *where it came from*
— if someone pushed those bytes by hand, the digest is still a perfectly valid,
perfectly immutable digest. The attestation is a signed claim by this repo's CI
workflow, so anything pushed by hand has no such claim and fails the check.

That covers a credential that can push to the registry, and stops a branch
writer getting *their own* bytes deployed. It proves **origin, not currency**,
though — "did CI on `main` build these bytes", not "are these the bytes this
release promoted" — so also confirm the tag still agrees with what you verified:

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
about a change merged to `main`. **Read the deploy bullet in
[`AGENTS.md`](../AGENTS.md#releases-and-image-publishing-351) before relying on
any of this** — it is the canonical statement of what each step does and does not
prove, and of why each flag is required.

The digest also appears at the bottom of the release notes, for humans.
Deployment configuration itself lives in the separate deploy repo, not here.

**Releases cut before this landed support neither step.** Their images were
published before attestation existed, so step 1 404s and step 2 finds nothing to
verify — the digest in the release notes is all there is, and it carries no
proof of origin. This applies to `v0.0.1` only; every release from the next one
on has both.

## Notes

- **Pull requests publish nothing.** The publish job only runs on `main`.
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
  The dispatch only accepts commits already on `main`, and **must itself be run
  from `main`** (the default branch in the *Run workflow* dropdown). The sha you
  type names the commit to build; the branch you dispatch from decides which
  workflow definition runs, and an image built from a branch dispatch carries
  provenance naming that branch — which the release workflow, and any deploy
  that verifies, both reject.
