# MCP writes share #307's idempotency unit, not the REST route (#809)

> **Rule**: the one-paragraph version lives in [`src/AGENTS.md`](../../src/AGENTS.md);
> this file holds the reasoning.

**Status:** accepted
**Date:** 2026-10-11

## What happened

No incident. `cluckwork_record_daily_entry` is the only write in phase 1 of the MCP epic
(#789). `/mcp` is exempt from `IdempotencyMiddleware` (#806), because no MCP client sends an
`Idempotency-Key` header, so the tool needed its own replay protection. Two options were
priced. The maintainer chose the second on #789 (2026-10-10).

## The rule

A write tool calls its module contract through `IdempotentUnit`, the #307
claim, execute and publish protocol that `IdempotencyMiddleware` also runs. The tool
requires an `idempotencyKey` argument. Its claim is keyed on the tool name, the calling
user and the key, and it fingerprints the arguments. The unit publishes only on an explicit
success. A domain refusal releases the claim, so the same call records once the farm fixes
the cause, such as a depleted flock. Breaking this either caches a refusal forever or lets a
model that resends a call write twice.

## Why not the obvious alternative

**Loopback to `POST /api/v1/daily-entries`.** This reuses the middleware without changes, but
the route accepts only session JWTs. The bearer selector sends ordinary routes to JWT
authentication, so an OAuth token gets 401. Accepting OAuth there would need a second
authentication design for one route. `AcceptOAuthTokens` alone would make the route
OAuth-only and lock out the SPA. Forwarding the caller's token is also the token passthrough
the MCP authorization spec forbids.

**A second copy of the protocol in the tool.** A copy drifts. The stolen-lease rollback, the
ambiguous-commit probe and the early connection release each came from a #307 or #269 review
finding, and a copy would have to keep all three. The unit is the middleware's own code,
moved. The #307 suites pass unedited on it.

**Publishing when the operation returns.** An MCP tool has no HTTP status, so "it returned"
is the tempting success signal. That caches `DailyEntry.FlockNotActive` and replays it after a
manager fixes the flock. `IdempotentResponse.Succeeded` is the operation's own verdict: a 2xx
for the middleware, a successful `Result` for the tool.

**An account-wide key, as most REST writes use.** A model chooses its keys, and two people's
agents can both choose `entry-2026-10-11`. With an account-wide key, the second agent's call
would be refused as a reused key or replay the first person's entry. The tool scopes the key
to the user, as `/api/v1/me` does.

## What changed for HTTP

The database protocol is unchanged: claim, one transaction, guarded publication, steal-loss
rollback, failure release and the lost-commit probe. One HTTP timing changed. The middleware
now writes the response after the unit has committed, released its connection and returned.
Before, it wrote inside the unit's recovery path. If that write failed after a committed
success, the old code entered the cleanup, found its own completed claim, and tried to write
the stored response a second time. Now the write failure goes to the upstream error handling,
and the client sees a failed request. The mutation is still durable and published, so the
client's retry with the same key replays it and writes nothing new.
`IdempotencyResponseWriteFailureTests` proves that retry. The change also means no database
connection is held while bytes go to a slow client.

## What this does NOT cover

- Every write tool runs this protocol through `McpWrite.RunAsync` (#1311, #1313). The
  operation key is `mcp:tool:<name>`, built from each tool's own constant;
  `McpWriteToolsTests.TwoTools_SameCallerKey_DoNotCollide` is guard 20.
- A Worker naming a flock outside their assignments is refused by the flock-scoped read
  (`IFlockLookup`) before the handler runs. `FlockScopeGuard` sits behind that read, and no
  MCP test reaches it on its own.
- A replay returns the first call's reply, including the flock name as it was then. It first
  checks that the caller can still see the flock, so a Worker unassigned since the first call
  gets the same not-found refusal a fresh call gets. Role and scope are checked by `[Authorize]`
  before the unit runs.

## How it is enforced

- `AtomicIdempotencyProtocolTests`, `StealLossConnectionReleaseTests`, `RetryBoundaryTests`
  and the other #307 suites: the HTTP adapter's behaviour, unedited.
- `RecordDailyEntryToolTests`: replay without a second `DailyEntryUpdate` audit row, a reused
  key refused, a refusal released, the REST route and the tool not aliasing, and the role,
  scope, Worker, cross-farm, farm-switch and audit-attribution checks.
- `RecordDailyEntryToolFaultTests`: the stolen-lease rollback and the lost commit
  acknowledgment on the MCP path.
- `McpToolSurfaceTests.RealTree_EveryWriteToolTakesAnIdempotencyKey`: guard 21, which also
  refuses a key with a default value.
- `IdempotencyResponseWriteFailureTests`: a failed response write after the commit, then a
  same-key retry that replays.
