# Implementation-plan review disposition — #606

Reviewed on 2026-08-26 against `04-implementation-plan.md`, the approved threat
model, and shipped SHA `f767dce0073aea17a7e4e8cd644224023d325c89`.

## Claude Sonnet — architecture

Session `aa9916c0-3c06-4996-af77-8a9b37553b0b` completed read-only. Claude's
plan-mode wrapper stored its detailed verdict in its private plan artifact and
printed only the summary; the driver recovered the session artifact rather than
counting hidden output as clean. No repository file was modified.

- **Actionable:** the assignment endpoint's existing error chain has a 409
  branch and an otherwise-422 fallthrough, so appending the new proof mapping in
  the wrong place silently turns the required 403 into 422. The plan now makes
  the missing-proof duplicate pair part of the named integration guard and adds
  an explicit mapping-order mutation.
- **Resolved by this review round:** Claude noted the earlier Qwen seat gap. The
  bounded Qwen retry completed during the same Phase-7 review and is recorded
  below.
- **Verified:** both handler signatures, validation ordering, step-up
  consumption, body-only idempotency hash, tenant-filtered flock lookup,
  seven-write caller inventory, optional `apiDelete` extension, SPA generation
  precedent, and the stale Help/glossary locations.

## Pi DeepSeek v4 Flash 0731 — contrarian security

Provider `vllm`, model
`deepseek-v4-flash-0731-nvfp4-dspark-ctx262144`, completed read-only within the
eight-minute bound and returned no blocker.

- **Actionable:** direct simulation provisioning must retain the assignment and
  audit event in one scoped EF unit and one `SaveChangesAsync`. The plan now
  states that atomicity explicitly. The suggested success-only split-save
  mutation was not adopted as a claimed guard: without a crash failpoint it
  finishes in the same state and cannot prove atomicity. The existing omitted-
  audit mutation remains honest red evidence; the one-save structure remains a
  required diff-review condition.
- **Actionable:** all non-2xx idempotency responses are uncached. The design and
  plan now state that a post-validation 404/409/422 spends its proof and a
  same-key retry re-enters with a fresh proof.
- **Actionable minor:** the design now records unchanged Owner self-targeting,
  qualifies INV-3's uniformity to syntactically valid requests, and makes the
  empty-`FlockId` 400 exception explicit.
- **Adjusted minor:** the audit test compares details to the actual restricted
  flock entity while separately pinning the established `Sim House A` topology,
  avoiding an unexamined duplicate literal as the sole source of truth.

## Pi Qwen3.8 27B Q5 XL — repository and tests

Provider `llamacpp`, model `qwen3.8-27b-q5-xl-220k-q8kv`, completed the bounded
retry successfully in about two and a half minutes. This closes the missing
Phase-7 repository/test seat; it does not rewrite the recorded Phase-4 timeout.

- **Actionable:** Task 2.2's exact audit snippet referenced an unbound `worker`.
  The plan now requires `var worker = cast.Workers[0]` before using
  `worker.UserId` and `worker.Email`.
- **Actionable:** email/flock audit mutations must be killed specifically by
  deserialized key/value assertions in the focused audit-details test, not by
  an unrelated existing actor test. The plan now assigns those killers exactly.
- **Actionable:** missing-proof setup, `openAssignments` success/catch generation
  gates, the existing Help test name, all positional `apiDelete` callers, and
  the glossary stale-phrase search are now explicit.
- **Rejected count finding:** Qwen initially reported eight raw writes by
  classifying line 459 as a POST, then its own recount conflicted. Direct source
  inspection plus Claude and DeepSeek independently establish seven writes:
  POST at the calls beginning on lines 280/284/432/487/490 and DELETE at
  314/461 in the shipped file; lines 312/459/466 are GETs. The plan retains
  seven and enumerates them by behavior rather than brittle line number.

## Reconciliation result

No reviewer found a blocker or requested an architectural change. Confirmed
findings were folded into the threat model and implementation plan. The plan's
four increments still cover all INV-1 through INV-10 obligations, and its
unfinished-marker scan is empty. It is ready for the owner's explicit implementation
approval; no implementation, commit, branch, push, or pull request has begun.
