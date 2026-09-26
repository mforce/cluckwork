# SPA E2E suite (#277) — Playwright over the #243 simulation fixture

The browser-side sibling of the k6 harness one directory up. k6 (#243) is
protocol-level load with no browser; this drives the **real SPA in a real
browser** against the **same** `seed --profile simulation` fixture, so the
screens under test are populated — real dashboards, reports, 90 days of history,
orders in every lifecycle state — instead of empty.

Two modes:

| | What it does | How to run |
| --- | --- | --- |
| **Smoke** (#385) | Key flow per persona + cross-cutting session/i18n/PWA guarantees, at two viewports (#814) | `npm test` |
| **Canary** (#386) | 1–2 browsers recording Core Web Vitals, optionally while k6 loads the backend | `bash run-canary.sh [--with-load]` |
| **Screenshots** (#549) | Captures the root README's four images into `docs/images/` | `npm run screenshots` |

**Screenshots are a third config on purpose.** That run WRITES FILES into the
repo, so folding it into `npm test` would rewrite committed images on every
smoke run and turn an unrelated green run into a dirty working tree. It is not
visual regression either — nothing asserts on pixels, and no gate checks the
images are current. See the header of `specs-screenshots/screenshots.spec.ts`
for the staleness contract.

**Run it on a freshly reset fixture.** `npm test` leaves its own rows behind — flocks and customers named `E2E …`, which sort ahead of the seeded `Sim …` ones — so a capture taken after a smoke run photographs test data and the daily-entry capture opens on an empty flock. `bash tools/simulation/reset.sh` first, then `npm run screenshots`, with no smoke run in between.

**The dashboard capture drives a DIFFERENT FARM**, and it is the only thing in
this suite that does. The simulation fixture cannot produce that image: its ~100
catalog flocks (#627) are placed, active and never file, so every day owes a
count nobody filed, no day is complete, and the trend strip draws fourteen
identical floor stubs — the spec's own "bars are not all one height" assertion is
what says so. `reset.sh` therefore provisions a second farm, `readme-farm`, and
seeds it with the DEMO profile: two houses with ~240 days of submitted history,
with today unrecorded on House 2. `cast.ts` exposes its Owner as
`readmeFarmOwner()`, whose `farmCode` the `signIn` fixture reads; every other
persona has no `farmCode` and keeps signing into `default-farm`. Full reasoning
in `tools/simulation/README.md` under "Two farms on this stack".

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
playwright.config.ts          smoke suite: workers=1, retries=0, two projects
  chromium                      1280x720, everything untagged
  chromium-phone                390x844, @phone only
playwright.canary.config.ts   canary: 1–2 workers, generous timeouts
specs/                        the smoke specs
  phone.spec.ts                 the @phone ones: tab bar, More sheet, overflow
specs-canary/                 the canary
src/
  browser.ts     which Chromium, and why there are two right answers
  cast.ts        personas from ../.sim-cast.json; cast label -> SPA role,
                 plus readmeFarmOwner() for the second farm
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

**Tag a phone-width test `@phone`, and nothing else.** `npm test` runs two
projects off one config: `chromium` at 1280x720 takes everything untagged,
`chromium-phone` at 390x844 takes `@phone`. They are complements, so a test runs
in exactly one of them — losing the tag does not widen a test's coverage, it
moves it to the other width. Use Playwright's structured form,
`test.describe("…", { tag: "@phone" }, …)`, never a substring of the title: a
tag in the title shows up in every reporter line and breaks on a reword. The
viewport is the *only* difference between the two projects, deliberately, so a
red in one and a green in the other is attributable to width alone.

**`nav` is the desktop sidebar and throws under the phone project.** Below 900px
the sidebar is `display: none` and BottomNav owns navigation, so a phone test
uses the `phone` fixture instead. That refusal is load-bearing rather than
tidy: three specs assert `toBeHidden()` on a sidebar link to prove a role gate,
and at phone width those links sit inside a *closed* dialog, which is hidden for
a reason the spec never claimed. For the same reason a sheet link is only
obtainable from the value `openMore()` returns — the open sheet is a
precondition the compiler enforces.

**Assert what the user sees and can do.** Not that a request was made, not that
an element exists. The export spec waits for bytes on disk; the sales spec
asserts the balance settled by the *"record payment"* button being withdrawn.

**Mutation-check anything new.** `npm run mutation` breaks one guarantee at a
time — at the network boundary for most, in the DOM for the a11y ones (#501),
through the CSSOM for the phone ones (#814) — and requires the spec claiming to
cover it to go red. A surviving mutant means the spec does not test what it
says; report it, do not delete the mutant. Every mutant declares its project,
and a width-scoped one additionally has to leave the *other* project green, or
its kill is reported as `NOT WIDTH-SPECIFIC` rather than counted.

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
path-filtered, three shards of about 5.5 min each since #966 — owner call
2026-08-08 after #433 broke it silently), so a PR that breaks a covered screen
or boot guard gets told. Everything else keeps
the standing #370 warning from `tools/simulation/README.md`: the `slow` and
`canary` dispatch modes, the k6 harness, and any change a docs-only path filter
skips — **nothing runs those for you**, and you are the only thing that will
notice breaking them.
