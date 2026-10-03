# AGENTS.md — `tools/simulation/`

Rules for the simulation harness and its Playwright suite. They extend the root [`AGENTS.md`](../../AGENTS.md), whose
conventions apply here too: one paragraph per rule, and a `→` link to the decision
record you must read before changing the rule.

## Playwright E2E

- **SPA E2E lives in `tools/simulation/ui/` (#277/#385).** Playwright drives the real built SPA using k6's `seed --profile simulation` fixture; `web/` stays Vitest. The suite enforces three rules, each with past catches: never hardcode a credential, never hardcode English, respect the farm clock. Reasoning about `inert` or the accessibility tree must use CDP (`src/ax.ts`); Playwright's own APIs do not model `inert` (#501). The `@phone` tag partitions two projects (#814): `chromium` at 1280 and `chromium-phone` at 390. At 390, the sidebar is `display: none` and the `nav` fixture throws; phone tests use `phone`. → [`277-spa-e2e.md`](../../docs/decisions/277-spa-e2e.md)
