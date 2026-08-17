# Design-time migration connection, fail-closed (#318)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file is the relocated rationale.

**Status:** accepted · **Date:** 2026-07

## The rule

`AppDbContextDesignTimeFactory` — used by `dotnet ef migrations add` and
`dotnet ef database update`, and **never** by the `migrate` verb, which uses the
built host's own config — has **no default connection**. An unset or blank
`CLUCKWORK_MIGRATIONS_CONNECTION` throws immediately, naming the variable.

## Why not a localhost default

The obvious convenience is a fallback like
`Host=localhost;Database=cluckwork;Username=postgres;Password=postgres`. That is
a *predictable* target: a developer who believes they are pointed at a scratch
database, and is not, runs DDL against whatever is listening on 5432. Failing
with the variable's name costs one line of setup and removes the whole class.

## The TLS floor applies here too

Every target is held to the **same allow-list floor as a Production boot**
([#261/#262](261-postgres-tls-floor.md)), reused via
`PostgresConnectionString.NormalizeAndValidate` — no second validator, because a
second validator is a second thing to drift.

The one exception is an explicitly acknowledged **loopback** development target:
`CLUCKWORK_MIGRATIONS_ALLOW_INSECURE_LOOPBACK=true` permits plaintext, but only
when the connection's host is `localhost`, `127.0.0.1` or `::1`, checked via
`IPAddress.IsLoopback`. Set against any other host it **fails** rather than
silently widening scope — the opt-out names loopback, so it grants loopback.
