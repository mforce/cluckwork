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
the Serilog pipeline by `RedactingLoggerPipeline`. It operates in two layers:

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

### 1b. Exceptions

An enricher only ever sees an event's *properties*. `LogEvent.Exception` is a
get-only property that Serilog renders **separately** (the `{Exception}`
output token calls `Exception.ToString()`; `Serilog.Formatting.Compact` writes
the same text as `@x`), and no enricher or `ILogEventFilter` can replace it —
the only pipeline element that can decide what the *next* element sees is a
sink. So exception text is covered by `ExceptionRedactingSink`
(`src/Cluckwork.Api/Logging/ExceptionRedactingSink.cs`): it runs the
exception's rendered text through the same content patterns as above and, when
that changes anything, rebuilds the `LogEvent` around a `RedactedException`
stand-in before forwarding. An exception with nothing sensitive in it is
forwarded untouched, keeping its real CLR type, stack trace and inner-exception
chain.

This matters in practice, not only in theory: Npgsql's exception messages
routinely carry the connection string, and the `Auth.RefreshRevocationFailed`
event below is logged *with* the exception that caused it.

**Coverage — exactly what is and is not behind that wrapper.**
`RedactingLoggerPipeline` builds the logger in two stages. Stage one is the
logger callers hold and owns only settings that must be evaluated where an
event is *created* (minimum levels, per-source overrides, destructuring); its
**single sink** is `ExceptionRedactingSink`. Stage two is a sub-logger holding
**every** sink, from both sources the app supports — `Serilog:WriteTo` in
configuration (this is how `appsettings.json` declares Console) and
`ILogEventSink` registrations in DI (`ReadFrom.Services`) — plus the configured
enrichers and then the property redactor. There is no supported way to attach
a sink to stage one, so a sink cannot opt out of either redaction layer, and a
sink added later — by an operator through configuration, or by a future
component through DI — is covered automatically with no further change.

Two things are deliberately **not** covered, and are not claimed to be:

- Anything a component logs through a Serilog logger it built **itself**
  rather than obtaining from this host's DI container. Nothing in this repo
  does that; a future addition that did would bypass the pipeline entirely.
- The **OpenTelemetry** exporter path (`Otlp:*`) carries traces and metrics,
  not Serilog log events. Exception detail recorded on an *activity/span* by
  the ASP.NET Core or EF Core instrumentation does not pass through this
  pipeline and is not redacted by it.

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

**Connection-string credentials specifically.** `Password=` / `Pwd=` values are
located by regex but their *extent* is decided in code
(`SensitiveDataRedactionEnricher.EndOfCredentialValue`), following ADO.NET's
own quoting rules — including that a quote inside a quoted value is escaped by
**doubling** it, which a `"[^"]*"` pattern terminates on early and so leaks the
remainder of the credential. An unterminated quoted value redacts to the end of
the string (fail closed). The scan is a single forward pass with a cursor that
never moves backwards, deliberately avoiding nested quantifiers: this code runs
on caller-controlled free text from the **anonymous** `/api/v1/client-errors`
endpoint, where a pattern with pathological backtracking would be a CPU
denial-of-service vector.

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
| `Auth.RefreshRevocationFailed` | The app's own attempt to revoke a refresh token (or a whole token family, following a replay) throws instead of completing — the safety action meant to lock a suspected attacker out failed to run. Covers the whole attempt, including the prerequisite lookup of the token's owner: a failure anywhere in it means the revoke did not happen. Emitted **once** per failed attempt. | `UserId` (when known — a failure in the owner lookup itself has no user id to report, and says so rather than staying silent). |
| `Auth.RateLimitRejected` | The per-IP fixed-window limiter rejects a request against the login or refresh policy with 429. Deliberately excludes the client-errors policy (#217) — that budget guards log-pipeline volume, not a credential. | `ClientIp`, `Path`. |

### Operational (non-credential) security events

These do not signal a credential attack — they signal that a shared-state
(Redis) dependency is degraded, or that the per-account report-concurrency
capacity ceiling has been breached. They carry no identity fields and are safe
to forward unredacted. A deployment backend **should** alert on a sustained rate
of either: each means the app is running in a degraded mode that a green health
check does not reveal.

| Event ID | Fires when | Fields |
|---|---|---|
| `SharedState.RedisUnavailable` | A shared-state (Redis) operation throws and the caller degrades: grant-replay fails closed, and the auth limiter and the report-concurrency lease fall back to their in-process implementations (#543/#544/#545). A sustained rate means a limiter is silently stuck in per-instance fallback. | `capability` (which port degraded). |
| `ReportConcurrency.OverCapacity` | A running report's lease lapsed (a reachable backend rejected the renewal) and no free slot was available to re-count it — the account is over its per-instance report-concurrency ceiling with this report on top (#545). Bounded and self-healing as reports finish; a persistent rate means the shared store is dropping slots under load or during outage recovery. | `capability`. |

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

`bootstrap-admin` (#283), `recover-admin` (#265), and `provision-account`
(#533) print a freshly generated temporary password to **stdout only** — never
through `ILogger`/Serilog, never OTLP. This is a stronger guarantee than
redaction: the value never reaches the logging pipeline at all.
`BootstrapAdminCommandTests`, `RecoverAdminCommandTests`, and
`ProvisionAccountCommandTests` capture each real subprocess's raw stdout and
assert the password appears exactly once and never in a Serilog-formatted line.
`SecretCliLoggingGuardTests` is the source-level proof that the property is
referenced exactly once in each command, on a `Console.Out` line. That static
half is load-bearing: redaction could make a forbidden logged-then-redacted
value invisible to subprocess capture.

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
- **Alert on the five authentication event IDs above** for brute-force
  (`LoginFailed` rate, `AccountLockedOut`), replay/theft
  (`RefreshTokenReplayDetected`, `RefreshRevocationFailed`), and abnormal
  rejection rates (`RateLimitRejected`); and on the two **operational** events
  (`SharedState.RedisUnavailable`, `ReportConcurrency.OverCapacity`) for a
  degraded shared-state dependency or a breached per-account capacity ceiling.
- **Treat `ClientIp`/`UserId` per its own retention policy** — they are
  legitimate correlation fields (the amendment to #273 requires them for
  alerting), not something this repo omits, but a deployment's retention and
  access-control posture governs how long they persist and who can query them.

None of the above is expressed in this repo's code, config, or committed
docs — see `AGENTS.md`'s "Host-agnostic repo" section.
