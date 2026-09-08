# Type dialog session helpers and retain the runtime default (#703)

**Status:** accepted — owner chose typed helpers plus a decision record on 2026-09-08.

**No incident for this residual risk.** Earlier PRs in [#703](https://github.com/mforce/cluckwork/issues/703) fixed abandoned-request successes affecting replacement dialogs. This final slice adds compile-time protection against future scope mismatches.

## Decision

In [useDialogAction](../../web/src/components/useDialogAction.ts), `openDialog`, `dismissDialog`, and `startLoad` accept the generic scope type `S` inferred from the screen's `dialogScopes`. A caller preserving a literal union gets a compiler error for an undeclared name. `RunOptions.dialog` already has the same type constraint.

`run` and `isPending` keep their `string` scope parameters so dynamic row and panel scopes, such as `assign:<user>:<flock>`, remain usable. No runtime branch or session behavior changes. [useDialogSession](../../web/src/components/useDialogSession.ts) still treats an unbegun scope as generation zero.

## Accepted limits

- A mistyped `run` scope without an explicit `dialog` option can fall outside `dialogScopes`. Its errors then route to the page and its `current()` predicate always returns true. If that action was meant to belong to a dialog, the abandoned-success bug can return for it.
- A declared dialog scope whose session has never begun claims generation zero and remains current until that scope is begun. Typing a name does not ensure the screen calls its session-edge helpers.
- Membership checking cannot detect choosing the wrong valid scope or inconsistent semantic use of a consistently declared name. Widening the scope type to `string`, bypassing it with type assertions, or calling from untyped JavaScript can also bypass this protection.

These limits are accepted. The compiler guard does not make abandoned successes impossible; screens still own correct session-edge calls and the per-statement `current()` checks that protect replacement-dialog state.

## Why no development assertion

A development-only check for a declared scope used before its first session could catch forgotten setup, but adds runtime code and a development/production difference. As described, it would not catch unknown `run` names: those take the page-action path and bypass session claiming. The owner chose the type-only change and recorded these remaining risks instead.

## Verification

The `restricts session edges to declared dialog scopes at compile time` case in [useDialogAction.test.ts](../../web/src/components/useDialogAction.test.ts) pins valid and invalid names for all three helpers, and keeps `run`'s scope unrestricted.

`npm run typecheck` checks its `expectTypeOf` and `@ts-expect-error` assertions. CI's `npm run build` also typechecks through `tsc -b`; the web pre-commit hook runs the typecheck script. Vitest executes the test body but does not validate TypeScript types, so a passing Vitest run alone does not prove this guard works.
