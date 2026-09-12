# Adversarial review of the synthesized design (#770)

Run after the arena synthesis, by an independent reviewer on a different model family
(`gpt-6-astra` via Codex CLI), briefed to find what is WRONG and explicitly told not to
re-check the six SDK/repo facts already verified in [`04-verified-findings.md`](04-verified-findings.md).

**Result: no blockers. Four major findings, one minor.** All five were checked against source by
the orchestrator before being accepted; all five hold. Four are now fixed in
[`01-design.md`](01-design.md) and [`02-guards.md`](02-guards.md). This file records what was
wrong and why it was missed, because that is the part a later reader cannot reconstruct.

## 1. The identity bridge screened parameters, not the dependency graph (major)

`McpCallContext` validates the scope the *tool* is constructed in. It says nothing about the scope
the tool's **dependencies** use. A tool taking only `McpCallContext` can inject a helper that opens
a scope via `IServiceScopeFactory`, copy `AccountId` into that scope's `TenantContext`, and resolve
a repository — whose fresh `FlockScope` is unresolved and therefore **unrestricted**. A
#388-narrowed Worker reads every flock in the farm. Both original guards pass: the tool takes the
context, and `IServiceScopeFactory` was not on the blacklist.

Why it was missed: the design reasoned about what a tool *is handed* and treated that as the
boundary. The EF query filter reads whatever `FlockScope` lives in the ambient scope
(`AppDbContext.cs:72`), so the boundary is actually the scope, and a second scope is a second
boundary nobody parsed.

Note the fix is not "add `IServiceScopeFactory` to the list" — a helper one level down reopens it.
Guard 7b walks transitive dependencies and a causal integration test proves it.

## 2. The extracted idempotency port had no success/failure contract (major)

Commit eligibility today is an HTTP 2xx (`IdempotencyMiddleware.cs:289`); failure rolls back and
**releases** the claim so a retry can execute (`:336`). An MCP tool call has no HTTP status, and the
SDK turns ordinary exceptions into `CallToolResult.IsError` inside its own dispatch — so the
`/error` handler is not the MCP adapter's error mapper.

An implementation publishing on "the delegate returned normally" therefore caches a **domain
failure**: record against a depleted flock → `DailyEntry.FlockNotActive` cached → a manager
reactivates the flock → the identical retry replays the cached failure indefinitely. Today's HTTP
path releases that claim and lets the retry run.

The design specified *key identity* precisely and left *outcome* unstated, which reads as complete
until someone implements it. Guard 24 did not catch this: it preserves the existing HTTP suites,
and the HTTP suites cannot exercise a path with no HTTP status.

**It also proposed a cheaper shape the design never considered:** have the write tool call the
existing HTTP endpoint over loopback with the caller's bearer and a tool-namespaced
`Idempotency-Key`. That avoids extracting #307 at all. The design's "Alternatives considered"
rejected hashing the JSON-RPC *envelope*, which is a different idea — so the loopback shape was
never priced against the extraction that the design itself calls its largest risk.

## 3. Name-addressed writes had no ambiguity contract (major)

Duplicate flock names are legal — `FlockRepository` states it, and its `ThenBy(f => f.Id)`
tie-break exists because of it. The design made "the model passes a flock *name*, not a GUID" its
headline ergonomic win and never said what happens when a name is not unique. First-match records
production against an arbitrary flock; `Single` throws on valid farm data. The unchanged handler
cannot detect either, because it receives a valid GUID.

## 4. No bounded-result contract, and two mechanisms were conflated (major)

Reusing a repository does not inherit the HTTP adapter's limits: the order cap lives in
`SaleEndpoints.cs`, not the repository, and `PaymentRepository` materializes every matching customer
group. An unbounded `read_receivables` serializes a large farm's whole book into a model's context.

Separately, and worse because it reads as covered: the design claimed `ReadsRequestBodyAttribute`
handled the request-body cap. It does not. That attribute classifies binding errors for
`BodyReadingEndpointTests`; the #309 cap reads `MaxRequestBodyBytesMetadata`, set by
`WithMaxRequestBodyBytes(...)`. **`/mcp` had no body cap at all.** Nor a rate limit — #143 is opt-in
per endpoint, and `/mcp` is one route carrying every operation.

## 5. Two guards could not go red on their stated mutations (minor)

- **Row 2.** Deleting the `CurrentUserContext.IsResolved` clause leaves the test green: an
  unresolved actor has `UserId == Guid.Empty`, so row 4's check still throws, and `Resolve` sets
  both properties together so no ordinary state isolates the clause. Fixed by testing the combined
  actor requirement honestly and marking `IsResolved` redundant defence.
- **Row 19.** Keying the MCP scope on `/mcp` does not alias HTTP and MCP — an HTTP daily-entry write
  still hashes its own route. The mutation collapses MCP tools onto each other, which is row 20's
  job. Fixed by mutating to the mirrored endpoint's route.

This is the fourth and fifth instance of the trap the design already documented three times. The
lesson holds and is worth restating: **a guard's mutation must be run, not reasoned about.**

## Claims the reviewer checked and found sound

- The middleware keeps mutation and publication in one transaction, uses single-attempt execution,
  probes ambiguous commits, disposes before loser polling, and skips `/error` re-execution. No
  demonstrated change to the `/me` scoping hash formula.
- Current handlers and repositories do **not** open secondary scopes today, and ordinary awaited
  continuations do not by themselves create finding 1's hole. It is a future-caller hazard.
- `MustChangePasswordMiddleware` rejects `/mcp` before dispatch; no tool-specific exemption needed.

## One suggestion it refuted

The orchestrator proposed investigating Serilog's one-line-per-request contract (#214) against a
JSON-RPC **batch** in a single POST. There is no such defect: **MCP removed batching in protocol
revision 2025-06-18.** Recorded so nobody re-opens it.

## Not covered

The reviewer accepted [`04-verified-findings.md`](04-verified-findings.md)'s six SDK facts without
rechecking, ran no mutation tests (no implementation exists), and notes that the SDK masking
exception detail does not prove a future custom error mapper safe — nor does anything here address
farm-controlled data (customer and flock names) flowing into a model's context as prompt content.
That last one is unexamined and is not tracked anywhere yet.

---

# Round 2 — attacking the fixes

Same reviewer, re-briefed to attack the **fixes** rather than re-review the original design, and
told explicitly that refuting its own round-1 suggestion was worth more than confirming it.
**Two major, two minor. All four verified and applied.** Round 2 confirmed the round-1 record
preserves all five findings without narrowing, and cleared revised guards 2, 29, 30 and 32.

## 1. Row 7b promised a guarantee its walk could not establish (major)

The round-1 fix said the walk covers transitive dependencies and therefore closes the secondary-
scope route. It does not. A helper registered as `sp => new Helper(() => sp.CreateScope())` exposes
only a *delegate* on its constructor — a constructor walk sees nothing. A static service locator
adds no edge. Detached background work need not change the graph at all. Factory registrations
already exist in the legitimate graph (`CluckworkIdentityServiceCollectionExtensions.cs:25`).

**This is the failure mode the round-2 brief specifically asked about: a fix that reads as complete
and is not, which is worse than the original gap.** The design now specifies a closed registration
model, adds a factory-mediated escape mutation, and — the important part — **stops claiming the
branch is unreachable**. It bounds the hazard, names the residue, and points at #787 as the
backstop that makes the residue fail closed rather than silent. That is why slice 6 is blocked
on #787 rather than merely related to it.

## 2. Row 31 tested an output bound while claiming a materialization bound (major)

The row said "no tool can materialize an unbounded set", but its mutation only removed the reply
cap. An implementation can call `ListCustomerBalancesAsync()`, apply `Take(cap)` to the result and
return correct continuation metadata: every response bounded, the entire farm's balance book
materialized. The guard stays green.

This is the neighbouring-mutation trap in its purest form — the mutation that is easiest to state
is not the one the invariant is about. Split into row 31 (bounded *reply*) and row 31b (bounded
*query*, asserted on rows consumed from the database). `PaymentRepository` takes no paging
arguments and materializes both grouped queries before building its list, so **reusing it at all is
the defect**; a paged balance query is required.

## 3. Row 19's mutation was ambiguous (minor)

The replacement said to key on the mirrored endpoint's route. Applied literally as
`OperationKey = "POST:/api/v1/daily-entries"` the namespaces stay distinct, because HTTP stores the
SHA-256 *digest* of that string. Now stated byte-for-byte: the computed `endpointHash`, exact method
and path including trailing-slash spelling, same account and caller key.

## 4. The loopback alternative over-promised (minor)

Round 2 partially refuted its own round-1 suggestion, which is exactly what it was asked to do. Two
gaps, neither fatal:

- A tool-prefixed `Idempotency-Key` is **not** a server-enforced namespace. HTTP accepts any
  non-blank key and hashes it, so an ordinary HTTP caller can send the identical header and collide.
- The middleware's replay path returns the cached status and body with **no replay discriminator**,
  so a loopback tool cannot honestly report `"replayed": true`. A replayed creation is
  indistinguishable from the original.

Both are now priced in the alternative rather than discovered during slice 1.

## What round 2 cleared

Revised guard 2 (can go red when both actor clauses are deleted), 29 (correct metadata activates
real enforcement), 30 (the pipeline has no global limiter, so removing the endpoint policy genuinely
removes throttling), 32 (two same-name flocks make first-match observably wrong). The idempotency
outcome contract is correct as written. For loopback: forwarding the caller's bearer preserves
tenant and actor resolution; the second credential-epoch read correctly rejects a credential revoked
between hops; only the inner request claims idempotency; the daily-entry endpoint has no rate-limit
policy so there is no double charge; `/error` re-entry keeps its existing skips.

## The pattern worth keeping

Round 1 found three guards that could not go red. Round 2 found two more — **in the fixes for round
1**. Every one was a guard whose stated mutation was adjacent to, but not identical with, the
invariant it claimed. The rule this repo already has is the right one and bears restating: *run the
mutation, do not reason about it.* None of these were caught by reasoning, including by the person
who wrote the rule into the same document.

---

# Round 3 — attacking the round-2 fixes

**Two major, one minor. All three verified and applied.** Round 3 also corrected two claims the
author added *during* round 2's fixes, which is the most useful thing it did.

## 1. The #787 backstop claim covered writes and was asserted for reads (major)

Round 2's fix said #787 — flipping `FlockScopeGuard`'s fail-open branch — makes the secondary-scope
residue "safe rather than silent". **True for writes, false for reads.** A read escape calls
`FlockRepository.ListAsync`, which goes through the EF global query filter; that filter reads
`FlockScope` directly and an unresolved `FlockScope` is unrestricted. `FlockScopeGuard` is never
called, so #787 changes nothing on that path.

The sharpest part: this contradicts **the author's own scoping of #787**, written the same day on
the issue itself — *"`FlockScopeGuard` is consulted by `RecordDailyEntry`… The MCP read slices do
not reach it."* The design asserted a protection its own sibling artifact ruled out. Two documents,
one author, opposite claims, neither flagged by the reviews that read them separately.

The residual **read** risk is now recorded as open rather than claimed closed.

## 2. Three operative statements still said "unreachable" (major)

Round 2 added an honesty note saying the branch is not unreachable, and left three statements
elsewhere — in *Alternatives considered*, *Open questions*, and *Deliberately not guarded* — still
asserting it is. An implementer reading any of those three would omit protection the new safety
argument depends on. All three now state the bounded claim; the superseded ones survive in this
review history rather than in the operative text.

## 3. Row 31b named a mechanism that cannot measure what it requires (minor)

Round 2's fix pointed at `ReportQueryBoundingTests`' `SqlCaptureInterceptor` and said the mechanism
"needs no invention". It records `(Sql, Parameters)` only. An implementation that fetches every page
in a loop and accumulates the whole book emits perfectly paged SQL and passes. Row 31b now requires
counting rows **consumed** — wrapping the returned `DbDataReader` and counting `Read`/`ReadAsync` —
with SQL inspection kept as supporting evidence.

## What round 3 cleared, and two counts it corrected

- **Row 7b is implementable.** Walking `ServiceDescriptor`s with reviewed factory edges and
  framework stopping points is practical. But the author's added claim of "**9** factory
  registrations" and the reviewer's own count of **10** disagreed — the discrepancy turns on which
  lambda parameter names a grep matches. The number is now deliberately absent: walk the built
  collection, do not grep, and do not trust a count in prose. This repo has been bitten by a bare
  count twice already.
- **Row 19 is correct.** The registered pattern is `/api/v1/daily-entries/` (group + `MapPost("/")`)
  and the middleware hashes the *actual request path*, so the fixture's trailing-slash spelling
  must match what it asserts.
- **Row 31's reply/query split is sound**, and grouping does not force client materialization —
  `PaymentRepository` already aggregates in SQL, so a paged balance query is achievable.
- The loopback namespace and replay-discriminator qualifications are sound.

## Three rounds, one pattern

| Round | Target | Guards found unable to go red |
|---|---|---|
| 1 | the synthesized design | 3 |
| 2 | round 1's fixes | 2 |
| 3 | round 2's fixes | 1 (plus 2 wrong claims added while fixing) |

Every round found defects **in the previous round's fixes**. The yield is narrowing — round 3's
findings are about claim precision rather than design shape — but it never reached zero. The
transferable lesson is unchanged and now has six instances behind it: **a guard's stated mutation
must be run, not reasoned about**, and a fix written confidently is exactly as likely to need one.

The loop stops here by decision, not because it ran out. What remains is not more review of this
document — it is running these mutations for real when the guards are written.
