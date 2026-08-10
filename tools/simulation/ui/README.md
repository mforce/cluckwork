# SPA E2E suite (#277) — Playwright over the #243 simulation fixture

The browser-side sibling of the k6 harness one directory up. k6 (#243) is
protocol-level load with no browser; this drives the **real SPA in a real
browser** against the **same** `seed --profile simulation` fixture, so the
screens under test are populated — real dashboards, reports, 90 days of history,
orders in every lifecycle state — instead of empty.

Two modes:

| | What it does | How to run |
| --- | --- | --- |
| **Smoke** (#385) | Key flow per persona + cross-cutting session/i18n/PWA guarantees | `npm test` |
| **Canary** (#386) | 1–2 browsers recording Core Web Vitals, optionally while k6 loads the backend | `bash run-canary.sh [--with-load]` |

**Playwright is never the load generator.** k6 stays the crowd.

## Quickstart

```bash
# 1. A seeded stack must be up. From the repo root:
bash tools/simulation/bootstrap.sh    # once — generates .env.sim + .sim-cast.json
bash tools/simulation/reset.sh        # wipe + build + seed + verify (~5 min)

# 2. Install this project's dependencies.
cd tools/simulation/ui
PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 npm install   # on NixOS — see "Browsers"
#                              npm install && npx playwright install chromium   # elsewhere

# 3. Run.
npm test                    # the smoke suite (~30s)
npm run report              # open the HTML report
npm run mutation            # prove the suite can actually fail (~3 min)
bash run-canary.sh          # Core Web Vitals on a quiet system
bash run-canary.sh --with-load   # ...and again while k6 saturates the backend
```

`npm test` refuses to run against a stack that is down or a fixture that is not
seeded, and says which (`src/preflight.ts`). That refusal is the point: a
half-seeded database otherwise fails as *"expected a table, found 'No stock
recorded yet'"*, which reads like a UI regression and sends people into `web/`.

## Browsers — the NixOS split

This box is NixOS, where Playwright's **downloaded** browser binaries are linked
against FHS paths that do not exist and fail to launch (the bundled Firefox does
not start at all). A **system** Chromium works and is what the suite finds.

`src/browser.ts` resolves, in order:

1. `CLUCKWORK_E2E_CHROMIUM` — an explicit path. A path that is not executable is
   a hard failure, never a silent fall-through: somebody named a binary, and
   quietly running a different one makes every result attributable to the wrong
   thing.
2. The first system Chromium present (`/run/current-system/sw/bin/chromium`, then
   the ordinary Linux locations).
3. Playwright's own download — which is correct, and is the path CI takes.

`CLUCKWORK_E2E_BUNDLED_BROWSER=1` forces (3). The run prints which one it chose.

## Layout

```
playwright.config.ts          smoke suite: workers=1, retries=0
playwright.canary.config.ts   canary: 1–2 workers, generous timeouts
specs/                        the smoke specs
specs-canary/                 the canary
src/
  browser.ts     which Chromium, and why there are two right answers
  cast.ts        personas from ../.sim-cast.json; cast label -> SPA role
  farm.ts        farm-local dates via Intl, mirroring web/src/lib/dates.ts
  i18n.ts        selector text read from the SPA's own en/es/tl catalogs
  api.ts         HTTP, for preflight and ground truth only — never assertions
  vitals.ts      Core Web Vitals collection
  mutants.ts     the mutation harness
  preflight.ts   globalSetup: is the stack up and the fixture real?
mutation-check.sh   baseline GREEN -> mutants RED -> restore GREEN
run-canary.sh       canary, optionally concurrent with k6
```

## Conventions that are load-bearing

**Never hardcode a credential.** Personas come from `../.sim-cast.json` — the
same git-ignored, runtime-generated file k6 reads. GitGuardian flags a
credential-shaped literal even in a test file, and even in a diff that removes
one.

**Never hardcode English.** Every user-facing string is translated (#182: en, es
and tl all render app-wide today). Selectors resolve through `src/i18n.ts`,
reading the SPA's own catalogs. A missing key throws rather than falling back to
English — a silent fallback would let an es spec assert an English string and
pass while proving nothing.

**Respect the farm clock.** The seeded farm is `America/Chicago`, behind UTC, so a
UTC "today" is in the farm's *future* for part of every day. Report endpoints
answer `400 Report.FutureRange` for it, and every date `<input>` in the SPA is
bounded by `max={farm today}`, so the browser will refuse to accept it — failing
as "could not fill the field", which points nowhere near the cause. Use
`farmToday()`. `tools/simulation/k6/dates.js` carries the full history; the short
version is that this exact bug made the k6 harness fail 12.4% of its requests,
but only between 00:00 UTC and farm midnight, so both recorded baselines passed.

**Assert what the user sees and can do.** Not that a request was made, not that
an element exists. The export spec waits for bytes on disk; the sales spec
asserts the balance settled by the *"record payment"* button being withdrawn.

**Mutation-check anything new.** `npm run mutation` breaks a guarantee at the
network boundary — the shape a real regression takes from the browser — and
requires the spec claiming to cover it to go red. A surviving mutant means the
spec does not test what it says; report it, do not delete the mutant.

## Known gaps, stated rather than implied

- **The PWA update prompt has no end-to-end coverage.** Rendering it needs a
  byte-different `sw.js` to install and park in `waiting`, and Playwright cannot
  provoke that: it intercepts the *initial* registration fetch but sees no
  request at all after `registration.update()` (measured both directions;
  `/sw.js` is served `no-cache`, so it is not a caching artefact). The logic is
  covered by `web/src/pwa/UpdatePrompt.test.tsx` and
  `registerServiceWorker.test.ts`; what is uncovered is the browser genuinely
  parking a second worker. The rest of #142 *is* covered end-to-end here.
- **The Worker persona asserts the write refusal, not #277's stated premise.**
  Flock assignment gates production writes (`422 FlockScope.NotAssigned`) and
  nothing else — every read of an unassigned flock answers 200. That is #388.
- **Interaction latency is `null` on screens with nothing to press**, and is the
  longest interaction rather than INP. See `src/vitals.ts`; the approximations
  are named as upper bounds on purpose.

## What runs for you, and what still doesn't

The **quick suite now runs on pull requests** (`.github/workflows/e2e-smoke.yml`,
path-filtered, ~3 min — owner call 2026-08-08 after #433 broke it silently), so
a PR that breaks a covered screen or boot guard gets told. Everything else keeps
the standing #370 warning from `tools/simulation/README.md`: the `slow` and
`canary` dispatch modes, the k6 harness, and any change a docs-only path filter
skips — **nothing runs those for you**, and you are the only thing that will
notice breaking them.
