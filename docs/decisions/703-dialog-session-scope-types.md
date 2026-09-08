# Type dialog session edges to a screen's declared scopes, accept the residual risk (#703)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md);
> this file is the relocated rationale (what shipped, why the short version was
> insufficient, what not to break).

**Status:** accepted
**Date:** 2026-09-08
**No incident.** #703's abandoned-attempt bugs and their fixes already shipped
across PRs 1–5; this record is forward-looking, narrowing the compile-time
contract of [`useDialogAction`](../../web/src/components/useDialogAction.ts)
after the fact, not fixing a defect in it.

## What happened

`useDialogAction`'s session edges — `openDialog`, `dismissDialog`, `startLoad`
— took a plain `string` scope, the same as `run`'s. `RunOptions.dialog` was
already typed to the screen's own `S` (its `dialogScopes` union), so a
misspelt dialog name passed to `run`'s `{ dialog }` option was already a
compile error. The three session-edge methods were not: a screen could call
`openDialog("cerate")` and the typechecker would accept it, opening a session
for a scope no dialog on that screen owns.

## The rule

`DialogAction<S>`'s `openDialog`, `dismissDialog`, and `startLoad` take `S`,
not `string` — narrowed to exactly the literal union a screen passes as
`dialogScopes`. `run`'s `scope` parameter, and `isPending`'s, stay `string`:
they carry dynamic row/panel scopes (`assign:<user>:<flock>`) that are never
members of `dialogScopes`, so narrowing them would reject legitimate calls.
Breaking this — widening a session edge back to `string`, or narrowing `run`
to `S` — reopens the typo hole this closes, or breaks every row/panel caller.

## Why not the obvious alternative

The obvious alternative is a runtime assertion: have `openDialog`/
`dismissDialog` throw or warn when called with a scope outside
`dialogScopes`. That was rejected — it is a behavior change (`AGENTS.md`'s
"no new runtime branch" for this slice), and it buys less than it costs: a
dev-only assertion checks membership at the same boundary the compiler
already checks for free, but does nothing for `run`, which cannot be
membership-checked without breaking dynamic scopes. The compile-time-only
fix is strictly narrower in scope and has no runtime cost or risk.

## What this does NOT cover

The compiler protects a **preserved literal union** reaching `openDialog`/
`dismissDialog`/`startLoad` — nothing more. A screen that widens its
`dialogScopes` array to `string[]` (dropping `as const`), or reaches an edge
through a type assertion or plain JS, gets no protection: the type system
only ever sees what the call site's own types say. Membership checking also
does not catch every misspelling: a screen that consistently misspells a
scope (declares `"cerate"` in `dialogScopes` and calls `openDialog("cerate")`
everywhere) type-checks cleanly — the compiler proves internal consistency,
not that the name matches what the dialog is actually called elsewhere.
Likewise, calling an edge with a *different, valid* member of the union
(`openDialog("edit")` where the screen meant `"create"`) is accepted; this is
not a typo the type system can see. A declared dialog scope a screen never
`openDialog`s claims session `0` and is treated as always current until its
first `begin` (#703 finding 3) — that behavior is unchanged and is not "safe"
in any stronger sense than it was before this change; the type narrowing does
not touch it. This is a compile-time-only contract: **no runtime assertion,
no fail-closed behavior, and no change to what any caller does at runtime.**

## How it is enforced

[`useDialogAction.test.ts`](../../web/src/components/useDialogAction.test.ts)'s
`"restricts session edges to declared dialog scopes at compile time"` test
pins the contract with `expectTypeOf` and `@ts-expect-error`, checked by
`npm run typecheck` (`tsc -b --noEmit`) — the same gate CI's build job and the
`web/`-touching pre-commit hook already run. Vitest itself does not exercise
this test at runtime; the compiler rejecting the invalid-scope calls is the
observable pass/fail.
