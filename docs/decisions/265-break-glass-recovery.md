# Break-glass recovery: `recover-admin` (#265)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> the **procedure and its verification drill** are in
> [`docs/runbooks/break-glass-account-recovery.md`](../runbooks/break-glass-account-recovery.md).
> This file is the design rationale.

**Status:** accepted · **Date:** 2026-07

## The rule

`recover-admin` is a one-off CLI verb on the same binary, with the same
run-then-exit shape as `seed` — but deliberately **NOT** environment-gated,
because it must work against a real Production database:

```bash
dotnet Cluckwork.Api.dll recover-admin --email <e> [--account <guid>] [--reason <text>]
```

For a locked-out account — the sole-Owner-lost-password case, with no email or
SMTP reset path — it does all of this in one transaction:

1. resets to a **freshly generated** temporary password, never one passed on the
   command line;
2. rotates the security stamp;
3. revokes every refresh token;
4. writes a conspicuous `User.BreakGlassReset` audit row carrying `--reason`.

The temp password is printed to **stdout only** — never the logger, never OTLP —
and the verb exits `0`. Failures go to stderr with exit `1` and change nothing.

## Why not accept a password on the command line

A password passed as an argument lands in the shell history, in the process
table while it runs, and in any process-level audit the host keeps. Generating
it inside the transaction means the only copy is the one printed to the operator.

## Why it is not environment-gated

The verbs that seed data refuse to run in Production on purpose. This one is for
the case where Production is exactly where the operator is locked out, so the
same gate would disable the tool at the only moment it matters. The audit row is
what makes that safe to allow: the use is loud, attributed, and carries the
stated reason.

## Where the code lives

`AdminRecoveryService` orchestrates; `IdentityProvider.BreakGlassResetAsync`
shares the reset/revoke core with `SetUserPasswordAsync`, so the two paths cannot
drift on what "reset" means.

Deliberately a **separate credential type** from
[#283 first-run provisioning](283-first-run-admin-provisioning.md) and from
#308 step-up — never conflated.
