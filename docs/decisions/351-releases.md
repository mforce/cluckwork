# Releases and image publishing — internals (#351)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md); this file is the relocated rationale (what shipped, why the short version was insufficient, what not to break).


Two stages, deliberately separate: **CI publishes, the release PR versions.**

1. **Every merge into main** → the `publish` job in `ci.yml` pushes the image that
   run just built, Trivy-scanned and boot-tested, named by commit:
   `ghcr.io/<owner>/<repo>:sha-<commit>`. No version, no git tag. Idempotent per
   commit, so there is no ordering hazard and nothing to race — two merges publish
   two different names.
2. **Merging the "Release vX.Y.Z" PR** (maintained by release-please) → a **draft**
   release with a generated `CHANGELOG.md`; the already-published image for that
   commit is **promoted** to `:vX.Y.Z`; and only then is the release published.

- **Promotion is a server-side retag of an existing digest** (`docker buildx
  imagetools create`), never a rebuild. **Do not "simplify" it into a build step**:
  a second `docker build` yields different bytes and a different digest, so the
  image carrying a version would be one no scan or smoke test ever examined. That
  is the whole point of #351. **`--prefer-index=false` is load-bearing** — that flag
  defaults to *true*, and with a single source the default wraps the manifest in a
  new image index with a **different top-level digest**, which silently defeats the
  guarantee. Do not drop it.
- **The release stays a draft until its image is promoted**, and GitHub withholds
  the git tag for a draft. So a failed promotion leaves no tag and no public
  release, instead of a version pointing at nothing. Publishing (`--draft=false`)
  is the last step. Draft is safe for release-please's own bookkeeping because
  manifest mode reads the current version from `.release-please-manifest.json`, a
  committed file, not from tags.
- **release-please runs twice per push, split around promotion.** Versioning is
  tag-independent (above); the **changelog boundary is not** — it resolves through
  the previous release's **git tag**, and a draft has no tag. So one invocation
  that cuts `vX.Y.Z` *and* computes the next PR computes it while `vX.Y.Z` is
  unresolvable: the boundary falls through to the full search depth and the PR
  restates every earlier release. Seen on the v0.0.2 cut (#409 proposed 0.0.3 with
  0.0.1's *and* 0.0.2's changelog) on a **green** run, because the version comes
  from the manifest and was right. It self-heals on the next push, which is why
  v0.0.1 passed unnoticed. Hence `skip-github-pull-request: true` on the cut job
  and a `groom` job with `skip-github-release: true` after `promote`.

  Two things on `groom` are not the obvious default:

  - `always() && !cancelled()` — `promote` is **skipped** on an ordinary push, and
    a skipped dependency would otherwise skip grooming on every non-release merge.
  - **The guarantee is a probe for the boundary release, not an inference from
    job results. Do not "simplify" it back to a `needs` predicate.**
    `needs.promote.result != 'failure'` looks sufficient and covers only the run
    that failed: on the *next* push the cut job succeeds with
    `release_created: false`, so `promote` is *skipped*, the predicate passes, and
    the still-untagged draft produces the duplicate one run later (#411 review).
    The predicate is kept only as a cheap fence.

    The probe is **scoped to `v<version from .release-please-manifest.json>`** —
    the one release release-please will try to resolve — and asks only whether
    *that* release is still an unpublished draft. Blocking on **any** draft was
    the first version and is wrong in the other direction: a hand-made draft
    under an unrelated tag would wedge grooming shut forever (#411 review,
    second round). It **skips rather than fails**, since an unrelated merge
    should not go red because a release is stuck, but warns and names the draft
    in the step summary.

    **It must probe with the App token, not `GITHUB_TOKEN`.** GitHub lists draft
    releases only to a caller with **push** access, and the job's own token is
    `contents: read` — so probing with it returns no drafts at all, the boundary
    draft looks absent, and the guard passes and grooms against the missing tag.
    A guard that silently never fires is worse than none (#411 review, third
    round). That is why the App token is minted *before* the guard and why
    `permission-contents: write` on it is not only for the changelog commit.

    A **404** on the manifest **proceeds** rather than skips: there is no
    previous release to resolve against, so nothing can be got wrong, and it is
    the state a repo is in before its first release — skipping there would
    deadlock it, since no grooming means no release PR, ever. But **a 404 and a
    rate-limit are not the same answer**: any other failure fails the step, so a
    transient API error cannot masquerade as "never released" and groom with no
    boundary check. Swallowing both into an empty version is a fail-open, and a
    guard that passes when it should block is the one outcome worse than having
    no guard.

  `groom` mints its own App token and **must** keep the `permission-*` downscoping
  — omitting those mints the union of every grant the App holds, silently (see the
  release-PR-token bullet below).
- **Repair path, in two parts.** Re-running the push event never helps —
  release-please reports `release_created: false` for an already-created release,
  so promotion would be skipped forever.
  1. *If the commit has no image at all* — a `[skip ci]` anywhere in a commit
     message suppresses the push run entirely, and GitHub matches those keywords
     **anywhere** in the message, so one can reach the squashed release commit via
     a changelog entry. There is then no run to re-run. Dispatch **CI** with the
     exact sha; it rebuilds through the same gates and publishes. It refuses any
     commit that is not already an ancestor of `main`, so it cannot be used to
     publish arbitrary branch content.

     **Dispatch it from `main`** (the default ref selector; `gh workflow run`
     without `--ref`). The *sha input* names the commit to build, but the *ref*
     you dispatch from decides which `ci.yml` definition runs — and promotion
     verifies the attestation with `--source-ref refs/heads/main`. Dispatched
     from a branch, the rebuild succeeds and publishes, then refuses to promote,
     and the only clue is the error at promote time. That strictness is the
     point (see the provenance bullet below); just don't trip over it mid-incident.
  2. Then dispatch **Release** with the tag (and the exact sha if the release's
     `target_commitish` is a branch name rather than a commit) to promote and
     publish the draft. It refuses a tag whose release is **already published** —
     promotion retags and rewrites notes, so aiming it at a live version would
     repoint it — and when the release records a real commit, that commit is
     authoritative: a supplied sha may only agree with it, never override it.
- **The bump comes from conventional commits**, so PR titles are load-bearing —
  squash-merge puts the title on main as the commit subject. The mapping is
  **damped while below 1.0.0**, via two settings in `release-please-config.json`:
  `bump-minor-pre-major` ("breaking changes only bump minor if version < 1.0.0")
  and `bump-patch-for-minor-pre-major` ("feature changes only bump patch if
  version < 1.0.0"). So today `feat!:`/`BREAKING CHANGE` → **minor**, and
  `feat:` along with **everything else** → **patch**.

  **Both are required and they are not interchangeable.** `bump-minor-pre-major`
  alone still lets a `feat` take the minor digit; the second setting is what keeps
  features on patch.

  **`initial-version` is a third, separate lever, and the two bump settings do not
  cover it.** The *first* release computes no bump at all — `Strategy.initialReleaseVersion()`
  returns `Version.parse(this.initialVersion)` or, absent that, a hardcoded
  `1.0.0`. So a fresh repo proposes **1.0.0** no matter what the pre-major
  settings say. That is exactly what happened here twice: PR #372 proposed
  `release 1.0.0`, adding the two bump settings changed nothing, and PR #374
  proposed `1.0.0` again. `"initial-version": "0.0.1"` is what fixes the first
  release; the bump settings govern every release after it. Don't diagnose one as
  the other.

  **This mapping changes silently at 1.0.0**, when both settings stop applying and
  the conventional defaults resume (`feat:` → minor, breaking → major). Reaching
  1.0.0 should therefore be deliberate — a `Release-As: 1.0.0` footer when you mean
  it — not a side effect.

  Note that `hidden: true` in `changelog-sections` only suppresses a type in the
  changelog *text*; it does **not** make it unreleasable. `DefaultVersioningStrategy`
  returns `PatchVersionUpdate()` for any commit set with no feat/breaking, and the
  only early exit is "zero conventional commits" — so a `chore:`-only merge does
  bump the patch digit. It lands in the pending release PR rather than in a
  release, so it costs a number, not a deploy.
- **The commit *body* is parsed too, and a parse error drops the whole commit** —
  no changelog entry, no bump, and the run reports success. **Never start a line
  with `something(` that has another `(` inside it**; indent it, bullet it, or put
  a word in front. Backticks do not protect it: the parser lexes such a line as a
  nested `type(scope)` header, and the scope admits no `(`. Only line *starts*
  matter, so `see foo(x) and bar(y())` mid-sentence is fine, as are `foo(bar)` and
  `(a(b))`. **Any** leading whitespace defuses it, tab as well as space — the
  hook's character class has to exclude all whitespace, not just the literal
  space, or it rejects tab-indented prose the real parser accepts (#411 review).

  ```text
  fix(x): summary                                    <- the whole commit is dropped

  The fence is the test:
  Assert.Single(AllMigrations()) fails when a second appears.
  ^^^^^^^^^^^^^^             ^
  │                          └─ a second "(" before the first one closes
  └─ line STARTS with  word(     ... so the parser reads it as type(scope):

  Pins(Shape()) covers the SQL.                      <- same shape, same problem
  ```

  ```text
  fix(x): summary                                    <- parses

  The fence is the test:

    Assert.Single(AllMigrations())                   <- indented off column 1

  fails when a second appears.

  - Pins(Shape()) covers the SQL.                    <- or a list marker
  ```

  `.githooks/commit-msg` catches this, and it is the right layer — **local commit
  messages are what lands**. It judges the message **as written**, not a
  comment-stripped view of it: git is not obliged to remove `#` lines or the
  scissors tail, a hook is never told which cleanup mode applies, and measuring
  `git commit -F` showed content below the scissors marker surviving under
  *every* mode including `--cleanup=strip` (#411 review). The only part always
  dropped is the `git commit -v` diff, and identifying it by a bare `diff --git `
  line is not good enough: that discards an **authored** patch excerpt, which git
  stores and the parser rejects (`+Assert.Single(AllMigrations())` fails exactly
  like the undecorated line). What distinguishes git's own section is its
  framing — `commit -v` emits the scissors marker, then its notes, then the diff
  — so the tail is dropped only when `diff --git ` is **preceded by a scissors
  line, with no blank line between them**. Match that marker **structurally —
  dashes, `>8`, dashes — never as a literal `#` string**: the surrounding notes
  are translated, and `core.commentChar` changes the prefix outright. With `;`
  configured, a literal `#` match missed git's own marker, so its verbose diff
  read as authored text and an ordinary commit was rejected.

  **Two of these findings compound to one accepted limit, stated plainly rather
  than chased further.** "Seen a scissors line, then *eventually* a `diff --git`
  line" was itself a gap — an unrelated later mention of `diff --git` truncated
  everything after it, including a genuine offender placed past that mention
  (#411 review). Tightened to require no blank line in between, which matches
  git's real template and closes that. But git strips a `-v` diff
  **unconditionally, regardless of cleanup mode** — measured true even under
  `--cleanup=whitespace` — so the message a real `-v` commit hands this hook is
  **byte-identical** to one where an author typed the same scissors-plus-diff
  shape on purpose. No check over content alone can tell those apart, and this
  hook does not try further: it is local and `--no-verify`-skippable already,
  exists to catch **accidental** breakage, and deliberately reproducing git's
  shape to smuggle a line past it takes the same intent as `--no-verify` — not
  something to keep patching around. With
  `squash_merge_commit_message=COMMIT_MESSAGES`
  and `squash_merge_commit_title=COMMIT_OR_PR_TITLE` (and merge/rebase also
  enabled), what release-please parses is:

  | part of the squash commit | comes from | hook sees it |
  |---|---|---|
  | body | every branch commit message, concatenated | **yes** |
  | subject, one-commit PR | that commit's subject | yes |
  | subject, multi-commit PR | **the PR title** | **no** |

  Both 2026-08 casualties sit in that table. `ef9a64b` (#407): a body line, from
  one of 17 concatenated commit messages — the hook covers that class. `b39e8fb`
  (#399): subject `Credential epoch: …` across 15 commits, so it came from the PR
  title, which no local hook sees — that half is the "PR title is the release
  note" rule plus reviewers.

  **Recovery is manual and not durable pre-merge**: editing the release PR's body
  does not touch the branch's `CHANGELOG.md`, and release-please force-updates
  both whenever its computed content changes. Repair with a follow-up PR against
  `CHANGELOG.md` plus `gh release edit <tag> --notes` **after** the release is cut.
- **Deploy by digest, never by tag**, and treat *obtaining* the digest and
  *verifying* it as two separate problems — the deploy side needs both, and one
  does not imply the other.
  - **Obtain:** every release carries an **`image.json` asset**
    (`gh release download <tag> -p image.json -R <owner>/<repo>`) with `image`,
    `digest`, `reference`, `tag`, `commit`, `repository`. A release asset, not a workflow
    artifact — it never expires and needs no `actions:read` on this repo. The
    digest is also in the notes for humans. **Do not make the deploy side parse
    prose, and do not have it resolve a tag.**
  - **Verify:** `ci.yml`'s publish job writes a **build-provenance attestation**
    (#354) against the pushed digest, stored as an OCI referrer beside the image
    (`push-to-registry: true`). The deploy side runs:

    ```bash
    # oci:// needs registry credentials; GHCR ignores the username
    echo "$GITHUB_TOKEN" | docker login ghcr.io -u x-access-token --password-stdin
    gh attestation verify oci://<image>@<digest> \
      --repo <owner>/<repo> \
      --signer-workflow <owner>/<repo>/.github/workflows/ci.yml \
      --source-ref refs/heads/main \
      --bundle-from-oci
    ```

    **All three flags are load-bearing and none is the default**, and each is
    easy to drop without noticing anything break — a missing one weakens the
    check silently rather than failing it.
    - `--bundle-from-oci` makes `gh` read the registry copy; without it the
      bundle is fetched from the **GitHub API**, so `push-to-registry` goes
      unused and the "no GitHub access needed" property is lost.
    - `--signer-workflow` binds the identity to the *workflow*; with `--repo`
      alone, **any** workflow here holding `attestations: write` satisfies the
      check.
    - `--source-ref` binds it to the *ref*, and this is the subtle one:
      `--signer-workflow` pins the workflow's **path**, and `workflow_dispatch`
      runs the workflow **definition** from whatever ref is selected. So without
      it, anyone able to push a branch could edit `ci.yml` there, dispatch it,
      publish and attest arbitrary bytes, and still match a path-only check.

    Consequence worth knowing: **a CI repair dispatch must run from `main`.**
    Dispatched from a branch it produces an image that will not promote, on
    purpose. Note also the consumer still authenticates to the **registry** for
    an `oci://` subject; the saving is no GitHub API access to this repo, not no
    credentials at all.

  **Holding a digest is not the same as knowing where it came from.** A digest
  identifies bytes exactly and cannot be moved — but bytes pushed by hand have a
  perfectly valid digest too, and a gate that checks digest *syntax* accepts
  them. Only the attestation distinguishes CI's bytes from anything pushed with
  `packages: write` (realistically a leaked token). That is why obtaining and
  verifying are listed as two steps and not one.

  **Where the fail-closed actually comes from.** `ci.yml` attests *before*
  uploading the digest artifact, so a failed attestation leaves no artifact —
  but that is a **within-a-run** property only, and it is easy to overclaim.
  Promotion finds the artifact by **name**, repo-wide, taking the first
  unexpired match, so an artifact from an earlier run of the same commit would
  still satisfy it. What makes it fail closed at *release* level is that
  `release-please.yml` **verifies the attestation before it retags**. Keep both;
  never let the ordering stand in for the check. And do not make either
  `continue-on-error` — an attestation nobody can rely on is worse than none,
  because it reads as coverage.

  **There are two gates, and they do not have the same strength — don't conflate
  them.**

  - **Promotion's check** (the verify in `release-please.yml`) is *inside a
    branch-editable workflow*. `workflow_dispatch` runs a workflow's definition
    from the selected ref, so someone who can push a branch can dispatch a copy
    with the verify deleted, running with that job's `contents: write` +
    `packages: write`. No check written inside a branch-editable workflow closes
    that — the attacker edits the check. The controls there are repo-level: who
    may push branches, who may run workflows, branch protection on `main`.
  - **Deploy-side verification** is *not* subject to that, and is the stronger
    of the two. It runs outside this repo, against the registry, and a
    branch-built image carries an attestation naming **that branch** as its
    source ref — which `--source-ref refs/heads/main` rejects. So a branch writer
    cannot get *their own bytes* deployed.

    But it proves **origin, not currency**, and that gap is reachable: the
    verify command answers "did this repo's CI on `main` build these bytes",
    **not** "are these the bytes this release promoted". Anyone able to rewrite
    a published release's `image.json` can point it at an **older, genuinely
    attested** digest. The deploy side reads `.reference` from that file,
    verifies it, and it passes — a **downgrade**, using bytes that really were
    CI's. Nothing in an attestation binds a digest to a release.

    A tag/digest comparison narrows that and is worth doing: check that
    `:vX.Y.Z` in the registry resolves to the digest **you verified**, and refuse
    if they differ. Compare against the digest in `reference` — the one the
    `oci://` subject named — and **never** against the asset's separate `digest`
    field. CI writes `reference` as `image + "@" + digest`, but nothing forces a
    *rewritten* asset to keep them equal: an attacker sets `reference` to an old
    attested digest and leaves `digest` matching the current tag, and a check
    reading `digest` compares the current digest to itself, passes, and deploys
    the old one. **Be precise about who that stops.** It defeats an
    attacker who can rewrite the release asset *and nothing else* — a leaked
    `contents: write` credential. It does **not** defeat the branch-dispatch
    actor above, who holds `contents: write` **and `packages: write`** (both are
    on the `promote` job, and a dispatched copy declares its own permissions).
    That actor moves the tag onto the older digest with the same
    `imagetools create` promotion itself uses, and the two then agree. Moving a
    tag is an ordinary registry operation — this bullet's own heading says so.

    So that actor is bounded by repo-level controls, exactly as promotion's
    check is: who may push branches, and who may run workflows. The registry
    half is separately closable with **immutable tags for `v*`** on the package —
    #354's third acceptance criterion, deliberately not in this PR because it is
    a registry setting rather than code.

  For the **canonical one-paragraph statement** of exactly what these gates do
  and do not prove — the summary that `README.md` and the `ci.yml` comment point
  at — see the **"Deploy by digest" bullet in [`AGENTS.md`](../../AGENTS.md)**. It
  is kept there as the single source so the three summaries cannot drift; do not
  restate it here.
- **Promotion reads the digest from CI's own run artifact, never by resolving
  `:sha-<commit>`.** That tag is mutable by anyone holding `packages: write`, and
  the merge commit is public seconds after merge while CI needs minutes to push —
  so resolving the tag would accept the first manifest to appear under that name,
  and a forged push in that window would be promoted and written into the release
  notes as the thing to deploy. **Do not "simplify" the promote step back into a
  registry tag lookup.** (That closed "there is no digest to deploy"; the
  provenance half — proving the digest is CI's — is the attestation above, #354.)
- **Adding a CI job that should gate a release? Add it to `publish.needs`.** The
  digest artifact is what promotion accepts as proof, and it proves exactly what
  `publish.needs` in `ci.yml` covers — no more. A job outside that list can be
  **red while publish still records a digest**, and promotion will then certify
  bytes that failed it. Nothing enforces the list: `needs` is hand-kept, and a new
  job defaults to *not* gating, which is the dangerous default. Treat "is it in
  `publish.needs`?" as part of adding any gating job.
- **Watch the version on the release PR, not just the changelog.** A
  `Release-As: X.Y.Z` footer on any commit reaching main overrides the computed
  version, and squash-merge can be configured to put a PR *body* into the commit
  body — so a contributor can force a version jump from a PR description. The
  human merging the release PR is the control; its title states the version.
- **Config lives in three files**, all machine-maintained — `release-please-config.json`,
  `.release-please-manifest.json`, and `version.txt`. **Never hand-edit the manifest
  or `version.txt`**; release-please owns them and a manual edit desynchronises the
  version it believes from the tags that exist.
- **`extra-files` (#458) extends that ownership to `web/.env.production`'s
  `VITE_APP_VERSION`** — release-please's `generic` updater bumps it in the same
  release PR as `version.txt`/the manifest, anchored by an `x-release-please-version`
  marker comment on the line above (not a same-line trailing comment — dotenv-style
  parsers don't strip inline `#` comments the way YAML/generic key:value formats do,
  so a trailing marker would have become part of the value). Vite loads
  `.env.production` automatically for a production build; no Dockerfile/CI plumbing
  needed beyond the file itself. Same "never hand-edit" rule applies to the value —
  only the marker's presence and the file's existence are this repo's to maintain.
- **The release PR is opened with a GitHub App token, not `GITHUB_TOKEN`** — and
  both reasons are load-bearing, so do not "simplify" it back.
  1. `GITHUB_TOKEN` **cannot open a pull request at all** unless the repo-wide
     *"Allow GitHub Actions to create and approve pull requests"* setting is on.
     That is how this first shipped, and the first real run failed with
     `GitHub Actions is not permitted to create or approve pull requests` after
     release-please had already pushed its branch.

     **Do not describe the off setting as the safeguard** — that setting governs
     `GITHUB_TOKEN` *only*, and does not constrain an App installation token,
     which is the whole reason this fix works. The trade is about **what a job
     must hold** to reach PR-write, not about capability existing at all:

     - **Setting on** removes a repo-wide *policy gate* on `GITHUB_TOKEN`. It
       grants nothing by itself — a job must still declare `pull-requests:
       write`, and the repo default is `read`. From then on any job that
       declares it can create and approve PRs with the ambient token and **no
       secret**. Scope that to runs whose `GITHUB_TOKEN` is write-capable at
       all: pushes, and `pull_request` runs from **same-repository** branches. A
       **fork** PR (this repo is public and allows forking) or a **Dependabot**
       PR receives a read-only token whatever the workflow declares, unless the
       separate fork / Dependabot write-token policies are turned on.

       That carve-out keys on the **event**, not on who triggered it — it covers
       the *direct* `pull_request` run only. A **`workflow_run`** fired off the
       back of a fork or Dependabot PR runs from the default branch with a
       normal writable-per-`permissions` token and full secret access, which is
       precisely what `dependabot-lockfix.yml` depends on to read the App key
       (see the design doc). So if that workflow — or any future privileged
       follow-on — ever declared `pull-requests: write`, turning the checkbox on
       would make it PR-write-capable with **no** fork/Dependabot policy
       involved. The exposure is not "any contributor can approve"; it is every
       job, present and future, that asks for the scope on a write-capable
       event.
     - **App token** leaves that gate closed, so no `GITHUB_TOKEN` anywhere can
       do it, and PR-write is reachable only by a job that explicitly references
       the App private key.

     **Do not restate this as "the capability lives in one job".** The private
     key is a *repository secret* and `permission-*` caps the returned token, not
     the key — so **any** job referencing that secret can mint the App's full
     grant. Two do today (release-please, and lockfix's `commit`). What makes
     that safe is that neither executes PR-controlled code; it is not a function
     of job count.

     `Pull requests: write` is indivisible — opening and approving a PR are the
     same scope — so **the release token can approve a PR**, and only the pinned
     action's behaviour stops it. Currently inert: `main` requires **zero**
     approving reviews and has **no** required status checks (verified against
     the live repo, 2026-08-02), so there is nothing for a rogue approval to
     satisfy. It stops being inert the day required reviews are enabled, which is
     why that capability belongs behind a secret rather than behind a declared
     permission any workflow can ask for.
  2. GitHub does not trigger workflows for anything `GITHUB_TOKEN` opens or
     pushes (recursion prevention), so a `GITHUB_TOKEN` release PR carries no
     `pull_request` checks. An App identity is exempt, so the release PR is
     built, tested and scanned like any other.

     **Do not inflate this into "the version commit would ship unverified".** It
     would not, and the gate that prevents it is elsewhere in this design:
     merging the release PR is a *human* action, so it produces an ordinary
     `push` to `main` that runs CI in full, and `promote` refuses to run without
     `published-digest-<sha>`, which `publish` records only after
     `build-and-test`, `web` and `image` have all passed. **Release verification
     rests on that artifact gate, not on PR checks.** What this reason buys is
     narrower and still worth having: seeing red *before* the merge rather than
     after, and not deadlocking releases the day required status checks are
     enabled on `main` — a checkless PR could never satisfy them.

  The token is **downscoped per permission** (`permission-contents`,
  `permission-pull-requests`, `permission-issues`) rather than taking whatever the
  installation holds. `issues: write` is not incidental — it is what creates and
  applies the `autorelease: *` labels release-please uses to recognise its own
  merged PR. The **same App also backs `dependabot-lockfix.yml`**, so that
  workflow pins `permission-contents: write` on its own mint: without the cap, the
  PR/issue grants added here would ride along into the token held by the job that
  pushes to a Dependabot branch. **Any new consumer of this App must downscope the
  same way** — the installation's permissions are a ceiling, not the intended
  grant. Note the failure mode: omitting `permission-*` entirely does not mint a
  *narrow* token, it mints the **union of everything the App holds**, silently and
  with no warning. Nothing enforces the cap today, which is #368.

  Setup: the App needs **Contents: RW, Pull requests: RW, Issues: RW**, and the
  repo needs `LOCKFIX_APP_CLIENT_ID` / `LOCKFIX_APP_PRIVATE_KEY` (shared with
  lockfix). Fails closed — a missing secret fails the mint step, so no release is
  cut with a fallback token.

  **`client-id`, not `app-id`.** `create-github-app-token` v3 deprecated `app-id`
  (`deprecationMessage: "Use 'client-id' instead."`), so every run annotated a
  warning until both mints moved over. They are *different values* for the same
  App — the App ID is a number, the Client ID a string (`Iv23li…`), both on the
  App's General page — so this needed a new secret rather than editing the old
  one. Neither is a credential; only the private key is. Don't "fix" a future
  deprecation by pointing `client-id` at `LOCKFIX_APP_ID`: the mint fails.
- Package visibility and the host's pull credential are **deploy-side** concerns
  (cluckwork-deploy#6), not this repo's.
