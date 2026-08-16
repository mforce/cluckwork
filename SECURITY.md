# Security policy

## Reporting a vulnerability

**Do not open a public issue** — that hands a working exploit to everyone reading
the tracker before the fix ships.

There is **no private reporting channel enabled on this repository today**, and
this file will not pretend otherwise. Contact the maintainer directly through
[their GitHub profile](https://github.com/mforce) and say only that you have a
security report; keep the details out of anything public until you have a private
channel to send them through.

No response-time SLA is published, because a published SLA nobody meets is worse
than none. Expect an acknowledgement once a maintainer sees the report, and a fix
or an explicit decision before any public disclosure.

> **Maintainer:** enabling GitHub private vulnerability reporting (Settings →
> Advanced Security → *Private vulnerability reporting*) replaces the paragraph
> above with a real channel: it stays private and gives a private fork to fix in.
> Deliberately off for now.

## Supported versions

Pre-1.0: **the latest release only**. There are no backport branches, and a fix
ships in the next version rather than as a patch to an older one.

## What is enforced automatically

→ [`docs/decisions/146-ci-security-gates.md`](docs/decisions/146-ci-security-gates.md)
for why each gate exists and what it alone misses.

- A **production** dependency with a **high or above** advisory fails CI, on every
  PR and again weekly. NuGet uses `dotnet list package --vulnerable`; npm is scoped
  to production with `npm audit --omit=dev` (dev-only advisories are logged, not
  blocking). Both run through
  [`.github/scripts/vuln-gate.mjs`](.github/scripts/vuln-gate.mjs), which **fails
  closed**: unreadable input, an unrecognised report shape or an unknown severity
  all block rather than pass.
- `dependency-review` checks the PR **diff**; CodeQL scans first-party source
  (advisory).
- **Container images** are scanned by Trivy, which fails the build on a *fixable*
  HIGH/CRITICAL. The runtime stage runs non-root and every base image is
  digest-pinned.
- **Third-party Actions are pinned to a full commit SHA.** A mutable tag means you
  review one thing and run another — the 2026-03 `aquasecurity/trivy-action` and
  2025-03 `tj-actions/changed-files` compromises both retargeted tags to
  secret-exfiltrating code.
- **GitGuardian** scans PRs for committed credentials.

## Muting an advisory

The only mute is a dated entry in
[`.github/security-exceptions.json`](.github/security-exceptions.json), with an
exact GHSA id and a **required** `expires`. An allowlist with no expiry is a hole
nobody revisits.

Prefer, in order: bump the package → pin or override the patched transitive →
only then an exception, naming the blocker and linking the PR that removes it.

## Verifying a release

Released images carry a build-provenance attestation. Deploy **by digest**, and
verify origin before you do — the exact commands, and a precise statement of what
they do and do not prove, are in [`docs/releasing.md`](docs/releasing.md#deploying)
and the deploy bullet of [`AGENTS.md`](AGENTS.md#releases-and-image-publishing-351).

## What this repo does not hold

Secrets, provider manifests and concrete environment values live elsewhere — see
**Host-agnostic repo** in [`AGENTS.md`](AGENTS.md#host-agnostic-repo-deployment-boundary).
`deploy/.env.example` carries placeholders only, including PEM armor with a
`replace-me` body rather than a usable key. Local development secrets live in
`dotnet user-secrets`, not in files.

If a secret is committed, **rotate first, scrub second**. Scrubbing alone leaves a
live credential in every existing clone and in the pull-request refs, which a
force-push does not reach.
