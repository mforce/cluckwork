# Container image hardening (#267)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file is the relocated rationale.

**Status:** accepted · **Date:** 2026-07

## The rule

- The runtime stage runs **non-root**: `USER $APP_UID`, uid 1654 — the base
  image's built-in `app` account.
- All three base images are **digest-pinned** (`@sha256:…`), kept current by
  Dependabot's `docker` ecosystem.
- A CI job builds the image and **Trivy** fails the build on a *fixable*
  HIGH/CRITICAL.
- Keep the full glibc base — tzdata + ICU per [#264](264-farm-timezone.md).
  Never chiseled, never Alpine.

## Why the Trivy action is SHA-pinned

`aquasecurity/trivy-action` was compromised in the 2026-03 supply-chain
incident: the attacker retargeted the *tag* to secret-exfiltrating code. Pin the
immutable commit, never the re-pointable tag.

This is the standing rule for every third-party Action in this repo, not a
one-off for Trivy — see [#146](146-ci-security-gates.md), which also records the
2025-03 `tj-actions/changed-files` compromise of the same shape.
