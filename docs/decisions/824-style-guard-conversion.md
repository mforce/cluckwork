# Retire style guards with a named successor (#824)

> **Rule** — the one-paragraph version lives in [`AGENTS.md`](../../AGENTS.md).

**Status:** accepted
**Date:** 2026-09-22

## What happened

No incident. The MUI conversion moves styles from `styles.css` into Emotion. Ten existing guards parse only that stylesheet, so deleting a converted selector can leave an assertion green after its subject has disappeared.

## The rule

Retire a style-guard assertion only in the change that deletes the rules it reads. Name its successor in that change, or record the loss of coverage. Keep production `className` values, including MUI `slotProps`, in the conversion census so a deleted CSS family cannot retain an unseen caller.

## Why not the obvious alternative

Keeping the old postcss test preserves a green check, but the check no longer observes MUI output. Replacing every style assertion with a DOM test also fails because jsdom does not load the stylesheet or compute layout. Theme configuration belongs in the theme test, source policy belongs in a source walk, rendered structure belongs in component tests, and geometry belongs in Playwright.

## What this does NOT cover

The class census does not prove that a declared rule has a visual effect, or require every CSS class to have a TSX caller. In particular, `styles.toolbar.test.ts` and the `table.data` subjects in `styles.num.test.ts` are already vacuous; retirement of their remaining CSS and assertions is currently unowned and needs a follow-up issue with named successors. The census admits a small exact set of semantic hooks and class families produced from closed domain values. The MUI source census checks object-style `boxShadow`, `textTransform`, and `elevation` syntax and fails on unsupported values. It does not interpret CSS-in-JS function results, `filter: drop-shadow`, hard-coded colour literals, or third-party component internals. `farmTheme.policy.test.ts` checks the resolved theme across every palette and mode, including all component elevation defaults.

The TypeScript CSP walk visits string and template literals throughout production source. It rejects a `url()` template with interpolation and any literal non-same-origin target. It does not evaluate strings assembled from fragments that never contain a complete decoded `url()` token individually.

## How it is enforced

`web/src/styles.conversion.test.ts` parses every production TSX file under `src`, parses every class selector in `styles.css`, and compares JSX and object-property `className` tokens with declared classes and exact semantic hooks. The same test holds the narrow MUI source census. `web/src/styles.csp.test.ts` scans CSS and production TypeScript sources. `web/src/theme/farmTheme.policy.test.ts` checks resolved theme policy.
