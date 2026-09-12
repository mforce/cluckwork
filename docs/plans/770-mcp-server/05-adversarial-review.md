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
