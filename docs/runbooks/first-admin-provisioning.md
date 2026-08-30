# Runbook: provisioning the first admin (`bootstrap-admin`)

**Issue:** #283 · **When to use this:** a freshly created database has no Owner —
the login page says so, or `seed --profile demo` exits `1` naming this command.

**Not this runbook:** an Owner exists but has lost their password. That is
[break-glass account recovery](break-glass-account-recovery.md) — `bootstrap-admin`
is a **silent no-op** once an Owner exists and will not reprint a password.

**Blast radius:** creates one user in the default account. Migrates the schema
first, so it holds DDL on the target database.

**Prerequisites:** the published image (or the source tree, for the dev forms),
and a credential that can migrate — see the production section below.

**Last drilled:** not recorded.

---

Base data — the default account, roles, default egg grades, packed-unit
conversions — ships inside the EF migrations, so it is already there. **No
credential is ever baked into the repo**, so the admin user is not.

All four forms below share the same properties:

- the generated one-time password goes to **stdout only** — never the application
  logger or the OTLP pipeline. A host's stdout collector (docker logs, journald, a
  platform log pipeline) may still capture it, so treat that output as sensitive
  while the password is valid;
- a first-run provisioning also prints the **farm code** (`on farm <slug>`) — #532
  made it a required sign-in input, so copy it from stdout along with the password.
  A re-run (the no-op bullet below) deliberately does **not** reprint it; to recover
  a code whose output was lost, run the read-only `list-accounts` verb.
- first sign-in shows a **Set your password** screen and refuses everything else
  until you pick your own;
- a re-run against an already-provisioned account is a safe no-op.

## 1. Production host

A production host has no source tree, no SDK and no compose file — only the
published image. Run the verb against the image directly. The invocation and the
credential it needs are a **single** fact:

```bash
# The --env-file MUST carry the migrator/owner credential (the #263 role split),
# NOT the runtime role. bootstrap-admin migrates the schema before it creates the
# Owner, and the runtime role has no DDL — point this at the runtime env file and
# it fails with `permission denied for schema public` after you were reasonably
# sure you had it right.
docker run --rm --env-file <owner-credential.env> \
  ghcr.io/mforce/cluckwork@sha256:<digest> \
  bootstrap-admin --email admin@example.com
```

Pass the **verb only** — the image's `ENTRYPOINT` is already
`dotnet Cluckwork.Api.dll` and `docker run` *appends* to it, so repeating the
binary makes `args[0]` be `dotnet`, which matches no verb and boots the web
server instead of provisioning anything.

Host-specific details — compose service names, network layout, concrete digests
or env-file paths — live in the deployment repo, not here.

## 2. Local Docker stack

```bash
docker compose -f deploy/docker-compose.yml run --rm app \
  bootstrap-admin --email admin@example.com
```

Same `ENTRYPOINT` rule: the verb only.

This form is correct for the dev stack, and it stops there — for the two reasons
that also make the production form look different:

- the dev compose `app` service has **no pinned container address**, so a
  one-shot `run --rm app …` can start beside the serving one; a production
  manifest that pins the app's address (to name it from a reverse proxy) makes
  that form fail with `Address already in use`.
- the dev stack uses **one credential for everything**, so `app`'s env file
  happens to hold DDL; a real deployment splits the migrator/owner and runtime
  roles (#263), which is why the credential has to be called out explicitly for
  production.

## 3. Compose dev database, API run from the IDE / CLI

```bash
ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project src/Cluckwork.Api -- bootstrap-admin --email admin@cluckwork.local
```

Note the `--` separator: it stops `dotnet run` consuming the arguments and passes
them to the app. (The Docker forms omit it because the image's `ENTRYPOINT`
already supplies the binary.)

`ASPNETCORE_ENVIRONMENT` matters: unset means Production, which fails the boot
against a plaintext local Postgres (the #261/#262 TLS floor).

This form reads `ConnectionStrings:Default` from the **API's** user-secrets,
which names the `deploy/docker-compose.dev.yml` database. It is the wrong form
for an Aspire-orchestrated stack — see form 4.

## 4. Aspire AppHost stack

[Aspire](aspire-local-development.md) starts its **own** PostgreSQL, on its own
data volume, with a **generated** password and the username `postgres` — not the
`cluckwork`/`cluckwork` pair the Compose dev stack uses. Aspire injects that
connection string into the `api` resource it launches, but `bootstrap-admin` is
a separate run-then-exit process the AppHost knows nothing about, so form 3
falls through to the API's own user-secrets and reaches for the Compose
credential. Against a running Aspire stack that surfaces as a connection error,
never as anything about the database being empty:

- `Failed to connect to 127.0.0.1:5432` when the two stacks sit on different
  ports (the pinned defaults), or
- `28P01: password authentication failed for user "cluckwork"` once they share
  one — same port, wrong credential.

Leave the AppHost **running** — its container *is* the database — and pass the
credential explicitly:

```bash
# Password: generated once by Aspire and stored in the APPHOST's user-secrets,
# not the API's. Username: postgres. Port: LocalPorts:Postgres (see the
# host-ports table in the Aspire runbook).
pg_password=$(dotnet user-secrets --project src/Cluckwork.AppHost list \
  | sed -n 's/^Parameters:postgres-password = //p')
[ -n "$pg_password" ]

ASPNETCORE_ENVIRONMENT=Development \
ConnectionStrings__Default="Host=localhost;Port=5433;Database=cluckwork;Username=postgres;Password=$pg_password" \
  dotnet run --project src/Cluckwork.Api -- bootstrap-admin --email admin@cluckwork.local
```

The environment variable outranks user-secrets for this one process; nothing is
written to either store, so the override lasts exactly one invocation.

Substitute the port this run is actually using. `LocalPorts:Postgres` can be
overridden per machine, per shell or per run, and an empty or unparseable value
returns Aspire to a random host port — in that case take the postgres endpoint
from the dashboard, or from `aspire describe` if the CLI is installed.

The two stacks are **separate databases**. An Owner provisioned through form 3
does not exist for form 4, and vice versa; each needs its own run.

## Verify

1. Sign in with the printed password **and the printed farm code**. The SPA must
   show **Set your password** and refuse every other screen.
2. Set a password. The app releases the rest of the UI.
3. Re-run the same command. Expected: a no-op — no new user, no new password.

## If it fails

| Symptom | Cause |
|---|---|
| `permission denied for schema public` | The env file carries the runtime role, not the migrator/owner one (#263). |
| The web server starts instead | The binary was repeated in front of the verb; `args[0]` was `dotnet`. |
| Boot fails on the TLS floor | `ASPNETCORE_ENVIRONMENT` unset against a plaintext Postgres (#261/#262). |
| Exits `1` naming `bootstrap-admin` while seeding | This is `seed --profile demo` (#500) telling you to run this runbook first. |
| `28P01: password authentication failed for user "cluckwork"` | Form 3 against the Aspire stack: the API's user-secrets carry the Compose credential, Aspire generated its own. Use form 4. |
| `Failed to connect to 127.0.0.1:<port>` | Nothing is listening there — the intended stack is down, or the other stack holds the port. |

## Drill

Safe on a scratch database only.

1. `docker compose -f deploy/docker-compose.dev.yml down -v && … up -d` — a
   database with no Owner. This drills form 3; form 4 is drilled
   against a reset AppHost volume (see the Aspire runbook's reset procedure).
2. Run form 3. Expected: a password and the farm code on stdout, exit `0`.
3. Sign in with the printed password **and farm code**; expect **Set your
   password**; set one.
4. Run form 3 again. Expected: no-op, exit `0`, no second password.
5. Update **Last drilled** above.
