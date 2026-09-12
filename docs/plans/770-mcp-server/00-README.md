# MCP server support — design record (#770)

**Status: design only. No code was written, and nothing here has shipped.**

#770 was a feasibility spike. This directory is the design that came out of it, produced by
running three independent design candidates in parallel and synthesizing them. Read it as
*what was intended at the time*, per [`../README.md`](../README.md) — if it ever disagrees
with shipped code, the code is right.

| File | What it is |
|---|---|
| [`01-design.md`](01-design.md) | The design. Caller's usage first, then shape, tradeoffs, alternatives, open questions. |
| [`02-guards.md`](02-guards.md) | 28 guards, each with the mutation that makes it go red, plus what is deliberately *not* guarded and why. |
| [`03-arena-synthesis.md`](03-arena-synthesis.md) | Which candidate became the base, what was grafted from the others, what was rejected. |
| [`04-verified-findings.md`](04-verified-findings.md) | The SDK and repo facts that were verified against primary sources, including four the spike got wrong. |
| [`05-adversarial-review.md`](05-adversarial-review.md) | Independent adversarial review of the synthesized design: four major findings, all verified and fixed, and why each was missed. |

## What changed relative to the issue

#770 read the work as "moderate — the tool surface is easy, the auth bridge is the hard part".
The tool surface being easy holds. Four of its premises did not survive verification, and all
four are recorded in [`04-verified-findings.md`](04-verified-findings.md) with evidence:

1. **The SDK already resolves the scope problem under `SessionMode.Stateless`** — it sets
   `mcpServerServices = context.RequestServices; ScopeRequests = false` itself. The setting
   worth guarding is `SessionMode`, not `ScopeRequests`; a guard on the latter is a false
   alarm that reddens on a mutation leaving the product safe.
2. **MCP does not function at all until `/mcp` is exempted from `IdempotencyMiddleware`** —
   every MCP request is a POST, so an unexempted pipeline returns 400 on `initialize` and
   `tools/list`. This is blocking even for the "read-only demo in days" the issue estimates,
   which did not account for it.
3. **#271 is touched.** `WithHttpTransport` registers three singletons and a hosted service
   *inside the package*, unconditionally. The blocker list is not extended — the service is
   inert under Stateless — but `AGENTS.md`'s instruction to re-derive the list by walking
   `src/` cannot see package registrations. Raised separately.
4. **`ModelContextProtocol.AspNetCore` 2.2.0 ships a `net10.0` target**, so the issue's open
   decision 5 (".NET 10 compat not yet verified") is answered: compatible.

## Decisions the owner made before this design

- Transport: HTTP streamable, per the issue's own recommendation.
- Tool surface: flock / stock / daily-entry reads, customer / order / payment reads, and one
  write — record daily entry. The write is what makes this the full bridge rather than the
  read-only slice the issue recommended starting with.

## Decisions still open

Listed in full at the end of [`01-design.md`](01-design.md). The one that gates real-world
usability rather than correctness: **MCP clients expect OAuth discovery and a refreshable
token, and Cluckwork's refresh is an HttpOnly cookie.** Tracked separately.
