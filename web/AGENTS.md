# AGENTS.md: `web/`

Rules for the React SPA. The root [`AGENTS.md`](../AGENTS.md) applies too.

## Text and styling

- When you remove a presentational transform (`text-transform`, casing), check every string it transformed in every locale.
- Help or glossary prose that names a control uses that control's label word in each locale. Look the label up per locale. Nothing checks this. → [`688-i18n-help-label-pairing.md`](../docs/decisions/688-i18n-help-label-pairing.md)
- Before you style a selector, count its call sites: `grep -rn "<class>" web/src --include='*.tsx'`. Zero means your change does nothing on screen. Before you delete one, grep the whole repo: `git grep -n "<class>" -- ':!web/src/styles.css'`. The Playwright harness selects by class too.
- Retire a style-guard assertion only together with the CSS it reads, and name its successor guard or record the coverage loss. A postcss assertion left behind after MUI replaced its selector stays green with nothing to test. → [`824-style-guard-conversion.md`](../docs/decisions/824-style-guard-conversion.md)

## Lint

- `npm run lint` runs ESLint from its own install in `web/lint/`. Run `npm ci --prefix lint` once.
- `web/lint/eslint-suppressions.json` may only shrink. Never run `--suppress-all` or `--suppress-rule` to admit a new violation. After you fix a suppressed violation, run `npm run lint -- --prune-suppressions`, and delete entries for deleted files by hand.
- Never fix an `exhaustive-deps` finding with auto-fix. A wrong dependency array can loop renders.
