# Design review disposition — #606

Reviewed on 2026-08-26 against draft `01-threat-model.md` and shipped SHA
`f767dce0073aea17a7e4e8cd644224023d325c89`.

## Claude Sonnet — architect

Dispatch record: session `b7c1e1c7-93b0-4bb5-8238-606edf2ef5f5`, completed
without permission denials, 30 turns, reported cost `$1.142241`.

- **Actionable / merge-blocking:** the direct simulation path promised audit
  detail parity but named no source for `flock.Name`, and existing tests checked
  only actor attribution. Revised the design to inject the existing
  `IFlockRepository` and `IAuditWriter`, use the known `SimActor.Email`, and pin
  `{ Email, Flock }` in a real-Postgres audit-detail test and mutation.
- **Actionable documentation follow-up:** the endpoint inventory said
  unassignment was “same” without spelling out its missing account, actor, and
  header inputs. Expanded the row with the exact interface and adapter changes.

## Pi DeepSeek v4 Flash 0731 — contrarian

Completed through provider `vllm`, model
`deepseek-v4-flash-0731-nvfp4-dspark-ctx262144`. Its read-only leash did not
include shell, so it returned its verdict on stdout and could not send through
agmsg.

- **Actionable trade-off / invariant correction:** `INV-4` incorrectly said a
  grant is consumed only by an executed mutation. Validation consumes at
  handler entry, so a later valid-proof 404/409/422 also spends it. Revised the
  invariant, idempotency explanation, and mutation table; explicitly forbade
  deferring consumption.
- **Already addressed:** independently found the same missing simulation audit
  detail guard as Claude.
- **Noise from incomplete source access:** warned that the seeded Owner might be
  built from literal roles. Shipped `SeedAsync` constructs `cast.Owner` with
  `RolesOfAsync(owner)`, and existing simulation audit-role assertions pin the
  Owner role. The design now states that preserved fact explicitly; no new
  product behavior or test is warranted.

## Pi Qwen3.8 27B Q5 XL — repository/test specialist

Provider `llamacpp`, model `qwen3.8-27b-q5-xl-220k-q8kv` loaded successfully and
remained active on the local GPU, but returned zero bytes after more than 27
minutes. The invocation was interrupted at the roster's hard runtime boundary
and is recorded as **did not answer**, not clean. It supplied no finding and no
evidence to reconcile.

## Reconciliation result

The two substantive reviews found two independent design defects: unbuildable
audit-detail parity and inaccurate grant-consumption semantics. Both are folded
into `01-threat-model.md`. No unresolved merge-blocking design finding remains.
The missing Qwen seat reduced Phase-4 repository-inventory confidence and was
scheduled for a bounded retry against the concrete implementation plan. That
retry completed in Phase 7; its result and reconciled findings are recorded in
`05-plan-review.md`. It remains recorded here as a Phase-4 non-answer rather
than being retroactively described as completed design-review evidence.

## Owner decision

Approved on 2026-08-26 after the two actionable review findings were folded
into `01-threat-model.md`. This approval advances planning only; no shipped
code, commit, branch, or pull request existed at approval time.
