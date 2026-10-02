# Container image hardening (#267)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file is the relocated rationale.

**Status:** accepted; the CI scan gate was reversed 2026-10-02 (see below) · **Date:** 2026-07

## The rule

- The runtime stage runs **non-root**: `USER $APP_UID`, uid 1654 — the base
  image's built-in `app` account.
- All three base images are **digest-pinned** (`@sha256:…`), kept current by
  Dependabot's `docker` ecosystem.
- A CI job builds the image and boots it. It does **not** scan it: the Trivy
  gate this decision first required was removed on 2026-10-02 (below).
  `image-scan.yml` scans the published image weekly instead.
- Keep the full glibc base — tzdata + ICU per [#264](264-farm-timezone.md).
  Never chiseled, never Alpine.

## The scan gate was reversed (2026-10-02, #1006)

The first version of this rule had CI run Trivy on every PR and every push to
`main`, failing on a *fixable* HIGH/CRITICAL, and `publish` needed that job.
On 2026-10-01 a HIGH OpenSSL CVE (CVE-2026-84782) appeared in the Microsoft
`aspnet:10.0` base image. No PR caused it and none could fix it until Microsoft
rebuilt the base. Every code PR's image check went red, every push to `main`
skipped `publish`, and the v0.1.5 release could not be promoted for lack of a
CI-recorded digest. The CVE was later judged unexploitable here.

Decision (owner): **no scan runs in CI and nothing is gated on one.** Widely used
projects (Keycloak, Harbor, Kyverno) scan on a schedule, report to the Security
tab and file issues; none block PRs on an image scan. There is no ignore file and
no accepted-risk file; an exception list would only recreate the gate with an
expiry date to maintain.

What replaces the gate is [`image-scan.yml`](../../.github/workflows/image-scan.yml),
weekly (Mondays 07:00 UTC) and on `workflow_dispatch`:

- It scans the published `:sha-<commit>` image of the newest `main` commit that
  has one, on `linux/amd64` and `linux/arm64`, for fixable HIGH/CRITICAL findings.
  The tag is resolved to one index digest first, and each architecture is scanned by its own manifest digest from that index.
- **Trivy is pinned by hand.** `aquasecurity/setup-trivy` is SHA-pinned and its
  `version` input is set (`v0.75.0`), because the input defaults to `latest`.
  Dependabot's `github-actions` ecosystem bumps the action SHA, **not** this
  input; raise it manually, or the scan runs an old Trivy and an old vulnerability
  database format.
- It uploads SARIF to the Security tab, one category per architecture.
- It keeps one issue per vulnerability id, open or closed
  (`.github/scripts/image-scan-issues.mjs`), matched by the id in its own title
  shape, never by body. A new id opens an issue; a known open one gets a comment.
  When a scan of **both** architectures completes without the id, the open issue
  is commented on and closed. A failed or partial scan changes no issue and no label.
- **Closing a scanner issue ignores that CVE; reopening it un-ignores.** An id
  whose only matching issue is closed is left alone: no new issue, no reopen, no
  comment. The scan adds an `ignored` label (created if missing) once, so the
  list shows which CVEs are still present but deliberately ignored. Reopening the
  issue makes the next scan remove the label and resume commenting. When an
  ignored CVE no longer appears, the label is removed, since it means "still
  present". If an open and a closed issue both match, the open one wins. Any
  closed issue counts, including one the scan closed itself because the CVE
  cleared.
  An issue is closed only when it has the scanner's title shape, both labels and
  the workflow's bot as author; a human-filed issue is never closed. Because the
  scan is limited to fixable HIGH/CRITICAL, "no longer found" means no longer
  reported as such, not that the package is clean.
- Fixes arrive as Dependabot base-image bumps (`docker` ecosystem), after which the
  next scan closes the issue.

**Accepted costs. Breaking any of these restores a risk the gate used to cover.**

- **Published and released images are not scanned before they ship.** They are
  scanned after the fact, so a vulnerability published since the last scan can go
  unreported for up to a week, and a release can ship an image nobody has scanned
  yet.
- **`publish.needs` no longer proves "scanned".** The digest artifact proves the
  image was built and boot-tested ([#351](351-releases.md)).
- **An ignore has no expiry, and a returning CVE is not re-reported.** A CVE that
  is fixed (issue closed by the scan) and later returns is not reopened or filed
  again; the `ignored` label is the only sign, and nothing notifies anyone.
- The weekly scan is the only coverage of OS packages in the image. The #146 NuGet
  and npm gates still block PRs that add a vulnerable library.

`workflow_dispatch` runs the scan on demand, for example to check whether a
base-image bump cleared a CVE or to look at the current `main` image before
merging a release PR. It is available, not a required release step.
`dry_run` prints the issue actions without touching any issue.

## Why third-party scan Actions are SHA-pinned

`aquasecurity/trivy-action` was compromised in the 2026-03 supply-chain
incident: the attacker retargeted the *tag* to secret-exfiltrating code. Pin the
immutable commit, never the re-pointable tag.

This is the standing rule for every third-party Action in this repo, not a
one-off for Trivy (`image-scan.yml` pins `aquasecurity/setup-trivy`) — see [#146](146-ci-security-gates.md), which also records the
2025-03 `tj-actions/changed-files` compromise of the same shape.
