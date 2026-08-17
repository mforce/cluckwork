# Both JWT keys are checked at boot, and the check is serving-only (#510)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file is the relocated rationale.

**Status:** accepted · **Date:** 2026-08

## The rule

`AddCluckworkIdentity` requires `Jwt:PublicKeyPem` **and** `Jwt:PrivateKeyPem`
to be non-blank **and to actually import**; a `Serving` process refuses to start
otherwise. The check takes a `ProcessRole` and is skipped for one-shot verbs,
which neither issue nor validate a token.

## The two traps it closes, both of which shipped

**`configuration[...] ?? throw` catches only null.** The shipped
`appsettings.json` carries `""` for both keys, and `??` does not fire on an
empty string — so the guard was decorative for every real deployment. Use
**`IsNullOrWhiteSpace`**, never `??`.

**Importing inside the `AddJwtBearer` delegate makes a corrupt key a per-request
failure.** The farm boots green, `/health/ready` passes, the container
`HEALTHCHECK` passes — and every authenticated request 500s. An orchestrator
sees a healthy instance that rejects every login. The import has to happen at
boot, where a bad key can still fail the process.

## Why not eager for every process

Making the check unconditional is a fresh [#331](347-process-role.md) — the
recurring defect where a serving-process guard aborts a one-shot verb, in this
case including `recover-admin`, the break-glass path. That is why it takes a
`ProcessRole` rather than running unconditionally, in the most
security-sensitive file of the set.

## How it is enforced

Pinned from both sides: four rows in `ProcessRoleGuardTests` (each key ×
missing/unusable), plus `OneShotVerbMinimalConfigTests`, whose environment
carries no `Jwt:*` at all.
