# Log redaction + security-event policy

Owns the app-side half of #273 (production log redaction + structured
security events). This is a **host-agnostic** document: it states what this
repo guarantees about what reaches the log pipeline, and what the deployment
side is *required* to provide — never a concrete provider, retention number,
or access-control configuration. Per `AGENTS.md`'s host-agnostic boundary,
choosing an actual log sink/aggregator, a concrete retention window, and who
gets access to it are deployment/ops-repo decisions, tracked separately from
this repo.

## What this repo guarantees (portable, enforced in code)

### 1. Redaction at ingestion

Every log event on every host — regardless of which sink it eventually
reaches — passes through `SensitiveDataRedactionEnricher`
(`src/Cluckwork.Api/Logging/SensitiveDataRedactionEnricher.cs`), wired into
the Serilog pipeline in `CluckworkTelemetryServiceCollectionExtensions`. It
operates in two layers:

- **Structural** — a property whose *name* matches a forbidden field
  (`Password`, `CurrentPassword`, `NewPassword`, `Token`, `AccessToken`,
  `RefreshToken`, `StepUpToken`, `Secret`, `ClientSecret`, `ApiKey`,
  `PrivateKey`, `Authorization`, `Cookie`, `ConnectionString`, `Phone`,
  `Address`, …) is replaced outright with `[REDACTED]`, regardless of type or
  content. See the class for the exact, current list — treat it as an
  allow-*more* list: adding a field name is always safe, removing one needs a
  reason.
- **Content** — every string-valued property, whatever its name, is scanned
  for recognizable patterns and the matched substring is replaced: email
  addresses, bearer/JWT-shaped tokens, ADO.NET-style connection-string
  credentials (`Password=...`), libpq URI credentials
  (`postgresql://user:pass@host`), and a conservative phone-number shape.
  This is what protects **caller-controlled free text** — the field name
  (e.g. `Message`) gives no hint about what it contains, which is exactly the
  #273 leak filed against `ClientErrorEndpoints` (the anonymous
  `/api/v1/client-errors` endpoint, #217).

**Known limitation, stated rather than hidden:** content-pattern redaction is
best-effort. It cannot reliably find every phone/address shape in
unstructured prose, and — more fundamentally — a value baked directly into a
message via C# string interpolation (`logger.LogWarning($"... {email}")`)
rather than a structured template hole (`logger.LogWarning("... {Email}",
email)`) has no *property* for the enricher to touch; Serilog exposes no API
to rewrite an already-rendered template. **Structured logging discipline —
every value that might be sensitive rides as a named template hole, never
baked into the message string — is therefore the primary control.** The
enricher is the safety net that catches what a call site forgot, not a
substitute for writing structured log calls in the first place.

### 2. Stable structured security events

Defined once in `Cluckwork.Application.Common.SecurityEvents` (shared by the
`Cluckwork.Api` and `Cluckwork.Infrastructure` layers that emit them) and
carried as the `{SecurityEvent}` structured property on every line below. The
names are permanent once shipped — a deployment backend's alert rules key on
them, so renaming one silently breaks every rule built against it.

| Event ID | Fires when | Fields |
|---|---|---|
| `Auth.LoginFailed` | Every unsuccessful `/auth/login` or `/auth/step-up` password check — unknown email, an already-locked account, or a genuinely wrong password. | `ClientIp` only. **Never** a user id or email — see "No identity-existence oracle" below. |
| `Auth.AccountLockedOut` | The specific failed attempt that crosses the configured lockout threshold (#128) and transitions the account from unlocked to locked. Fires **once** per lockout episode — a later attempt against an already-locked account re-fires `LoginFailed` but not this. | `UserId`, `ClientIp`. |
| `Auth.RefreshTokenReplayDetected` | A presented refresh token is found already revoked/rotated *and* the #176 grace-replacement check rules out a benign concurrent retry — a genuine reuse of a dead token. | `UserId`, `ClientIp`. |
| `Auth.RefreshRevocationFailed` | The app's own attempt to revoke a refresh token (or a whole token family, following a replay) throws instead of completing — the safety action meant to lock a suspected attacker out failed to run. | `UserId` (when known). |
| `Auth.RateLimitRejected` | The per-IP fixed-window limiter rejects a request against the login or refresh policy with 429. Deliberately excludes the client-errors policy (#217) — that budget guards log-pipeline volume, not a credential. | `ClientIp`, `Path`. |

### No identity-existence oracle

The API already collapses "no such user", "account locked", and "wrong
password" into one generic 401 (`Identity.InvalidCredentials`) so a caller
can't enumerate accounts. `Auth.LoginFailed` preserves that at the log layer
too: **all three branches emit the identical event with the identical field
set** — no user id, no email, ever, on this event. `Auth.AccountLockedOut` is
a separate event that only ever fires on the "wrong password, and this
attempt crossed the threshold" branch, so its mere presence can't be used to
tell a nonexistent email apart from a real, wrong-passworded one.

### Never-logged invariant: generated one-time passwords

`bootstrap-admin` (#283) and `recover-admin` (#265) print a freshly generated
temporary password to **stdout only** — never through `ILogger`/Serilog,
never OTLP. This is a stronger guarantee than redaction (the value never
reaches the logging pipeline at all, so there is nothing for the enricher to
catch if a future edit accidentally routed it through `ILogger`). Regression
coverage: `BootstrapAdminCommandTests` /
`RecoverAdminCommandTests` capture the real subprocess's raw stdout (which
also carries Serilog's own Console sink output in the same stream) and assert
it contains *only* the command's explicit `Console.Out` lines — a stray
structured log line would show up in that same capture, not vanish silently.

## What the deployment/ops repo must provide (requirement, not configuration)

This repo does not choose or configure any of the following — it only
guarantees the events above exist and are safe to forward. A deploying
operator must:

- **Pick a log sink** with defined **retention** (how long events are kept)
  and **access control** (who can read them) appropriate to the environment.
- **Bound cost/volume** — a sampling or budget policy so a hostile or
  malfunctioning caller (the anonymous `/client-errors` endpoint is the
  obvious vector, already rate-limited and byte-capped at the app layer, #217)
  can't run up an unbounded aggregation bill.
- **Alert on the five event IDs above** for brute-force (`LoginFailed` rate,
  `AccountLockedOut`), replay/theft (`RefreshTokenReplayDetected`,
  `RefreshRevocationFailed`), and abnormal rejection rates
  (`RateLimitRejected`).
- **Treat `ClientIp`/`UserId` per its own retention policy** — they are
  legitimate correlation fields (the amendment to #273 requires them for
  alerting), not something this repo omits, but a deployment's retention and
  access-control posture governs how long they persist and who can query them.

None of the above is expressed in this repo's code, config, or committed
docs — see `AGENTS.md`'s "Host-agnostic repo" section.
