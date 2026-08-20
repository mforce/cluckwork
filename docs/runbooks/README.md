# Runbooks

Procedures a human follows under pressure: break-glass credential recovery, first
admin provisioning, local-stack orchestration, restoring from backup, verifying
an accessibility fix on real assistive tech.

| Runbook | When |
|---|---|
| [Aspire local development](aspire-local-development.md) | Run, observe, persist, or safely reset the local PostgreSQL, Redis, API, and Vite stack. |
| [First admin provisioning (`bootstrap-admin`)](first-admin-provisioning.md) | A fresh database has no Owner. |
| [Break-glass account recovery (`recover-admin`)](break-glass-account-recovery.md) | An Owner exists but is locked out. |
| [Backup & restore](backup-and-restore.md) | Disaster-recovery dump, and putting one back. |
| [Screen-reader verification](screen-reader-verification.md) | An announcement change needs real assistive-tech confirmation (jsdom cannot). |

## What separates a runbook from a wiki page

**The verification drill.** A procedure never executed against a real system is a
hypothesis. Every runbook here ends with a drill — the steps to *prove* it works,
safe on a scratch environment — and the date it was last run.

The failure this directory prevents is a confident procedure that references a
flag renamed two releases ago, discovered at 03:00 by the one person who can fix
it.

## Rules

- Written for someone who is **not** the author, at their worst hour. No implicit
  context, no "obviously".
- Exact copy-pasteable commands, with the expected output shown.
- Every destructive step states what it destroys and how to undo it — or says
  plainly that it cannot be undone.
- **No environment values.** Connection strings, hostnames, CIDRs and credentials
  live in a secret store, with only its wiring in the deployment repo — never
  here and never in this repo's history. A runbook names the *variable*.
- Re-run the drill after any change to the code paths it touches, and update
  **Last drilled**. `not recorded` is the honest value until someone has actually
  run it; do not date a drill you did not perform.

Copy [`TEMPLATE.md`](TEMPLATE.md) to start one.
