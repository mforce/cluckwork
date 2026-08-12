# First-run admin provisioning: the `bootstrap-admin` verb (#283)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md); this file is the relocated rationale (what shipped, why the short version was insufficient, what not to break).

**First-run admin provisioning (#283):** `bootstrap-admin` is a fourth one-off CLI verb, same run-then-exit shape as `seed`/`migrate`, always available in Production (same posture as `recover-admin`): `dotnet Cluckwork.Api.dll bootstrap-admin --email <e>`. It migrates the schema (idempotent, like the other verbs), then — **only if the default account has no Owner yet** — creates one with a **freshly generated** password and `ApplicationUser.MustChangePassword = true`, printed to **stdout only** (never the logger/OTLP, identical rule to `recover-admin`). A re-run against an already-provisioned account is a silent no-op: no second Owner, no password reprinted. `FirstRunAdminService` orchestrates (mirrors `AdminRecoveryService`'s CLI-wrapper-calls-a-service shape); `IIdentityProvider.CreateUserAsync` gained an optional `mustChangePassword` parameter (default `false` — an ordinary Users-page-created user is never gated) that this is the only caller to pass `true`. While the flag is set: the JWT carries a `must_change_password` claim (`JwtTokenService`), `MustChangePasswordMiddleware` refuses **every** endpoint except `auth/change-password` and `auth/logout` with 403 (placed before `UseAuthorization`, so it applies uniformly regardless of an endpoint's `AuthPolicies` tier, and before the idempotency middleware, so a blocked write burns no key), and the SPA's `ProtectedRoute` renders a **"Set your password"** screen (`SetPasswordPage`, reusing `/auth/change-password` — the operator already knows the printed password as their "current" one) instead of the app shell, on every route. Any successful password reset (self-service change, an Owner's `SetUserPasswordAsync`, or break-glass) clears the flag — a single invariant in `IdentityProvider`, not re-derived per path. **Deliberately a separate credential type** from `recover-admin`'s temp password and from #308's browser step-up re-confirmation grant — different audience (pre-auth shell access vs. an authenticated Owner), different lifetime, never conflated; do not treat any of #283/#265/#308 as covering another.

## The verb's own audit row names a system actor (#500)

`bootstrap-admin` creates the first Owner, so there is no human to attribute that
creation to — not even the user being created, who does not exist yet. Since #500
`AuditWriter` refuses an event with no resolved actor rather than falling back to
`"(unresolved)"`, so `FirstRunAdminService` declares what it is:
`currentUser.ResolveSystemActor(SystemActors.BootstrapAdmin)`, immediately after
its existing `tenant.Resolve`. The `User.Create` row therefore carries
`ActorEmail = "(bootstrap-admin)"` and `ActorUserId = Guid.Empty` — the same id
those rows always had; what changed is that the label is now chosen deliberately
instead of defaulted by a fallback nobody could see.

This is still a parenthetical placeholder, which is the shape #500 complains
about. It is kept because it is honest, it is not *"unresolved"*, and the row
never renders on any of the five #494 provenance screens (a user's creation is
not one of them). `recover-admin` does the same with `SystemActors.BreakGlass`.

**`bootstrap-admin` is now also a prerequisite of `seed --profile demo`** — the
demo fixture is signed by the Owner this verb provisions, so demo refuses to run
without one. Note the seam that creates: this verb prints its generated password
**only on first provisioning**, so on a re-used database the demo fixture is
attributed to an Owner whose credentials may be lost, and the route back is
`recover-admin` rather than another `bootstrap-admin` (which no-ops silently).
See [`280-seed-and-simulation.md`](280-seed-and-simulation.md).
