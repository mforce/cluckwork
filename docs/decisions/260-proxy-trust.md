# Proxy-trust boot guard (#260)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file is the relocated rationale. Why the guard is scoped to the serving
> process and not to the CLI verbs is in [#347](347-process-role.md).

**Status:** accepted · **Date:** 2026-07

## The rule

Behind a reverse proxy or edge, HSTS (#144) and the per-IP login limiter (#143)
only work if the app trusts the proxy's `X-Forwarded-*` headers — which it does
**only** for networks listed in `RateLimiting:TrustedProxies`.

An **empty list in Production fails the boot**, rather than silently running
with inert HSTS and a one-bucket limiter.

## Why failing the boot rather than warning

Both degradations are invisible from outside. HSTS is emitted or not; a limiter
keyed on the proxy's own address still *works*, it just puts every client in one
bucket — so a login-attempt flood from one IP is indistinguishable from normal
traffic, and the protection reads as present. A warning in a log nobody reads at
deploy time does not close that.

## The opt-out, and its one legitimate use

`RateLimiting:AllowNoTrustedProxies=true` — for a rare direct-TLS deploy with no
fronting proxy, where there are no `X-Forwarded-*` headers to trust in the first
place. It is not a way to get a misconfigured proxy deployment to start.

## Scope

`ProcessRole.Serving` only, so `migrate`/`seed`/`recover-admin` are unaffected —
see [#347](347-process-role.md), which also records the related defect: the
**empty** list was correctly serving-only while the **same key malformed**
aborted every verb.

The concrete edge CIDR is deploy config, and lives in the separate
deployment/ops repo — never here.
