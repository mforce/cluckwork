# 674 — Adopt a UI component library, and which one

**Status:** accepted (2026-09-13) · **Issue:** #674 sequence step 1 · **Supersedes:** nothing;
**amends** `specs/technical/tech_spec.md` §8.1 (KD-6), which prescribed Tailwind + shadcn/ui.

## The decision

Adopt **MUI** (`@mui/material`) as this app's component library. MUI's theme is driven by the
CSS custom properties already in `src/styles.css`, read back off the document at runtime
(`src/theme/farmTokens.ts`, `src/theme/FarmThemeProvider.tsx`) — so the stylesheet stays the
single source of truth for colour and the four farm palettes (#149/#586) reach MUI with no
per-palette configuration.

Tailwind + shadcn/ui is **declined** (owner, 2026-09-13), not deferred. It was the live
alternative while the choice was open, and this record keeps the evidence for it because the
reasons it lost are not obvious — but it is a closed question, not a queued one. Re-opening it
needs a new decision record, not a follow-up ticket.

## Why there was a decision to make at all

The hand-built approach was a convention nobody chose. Searched `AGENTS.md`, `CONTRIBUTING.md`,
all 36 records in `docs/decisions/`, `web/README.md` and `specs/`: no rule prohibits a UI
library, and `web/README.md:11` says "No CSS framework **yet**" — a status line, not a rule.
Meanwhile §8.1 prescribes Tailwind + shadcn/ui and Recharts, none of which is installed. **The
spec and the code disagreed and neither said so**, which is how each slice re-litigated it by
default and always answered "build it".

What that cost, measured: `NamedEntityPicker` 1,147 lines with 1,852 lines of test across four
files; `Dialog` + `useConfirm` + four `useDialog*` hooks ~640 lines; `usePagedList` 381;
`styles.css` 3,676. #501 needed CDP to test `inert` — the kind of problem you inherit by owning
the primitive.

## Why MUI rather than the alternatives

The owner's stated criteria were, in order: longest track record and most trusted, fully
featured, and keep the farm palettes if they come cheaply.

| Library | First shipped | Downloads/week | Fully featured |
| --- | --- | --- | --- |
| **MUI** | **2014-10** | 7.2M | yes |
| Ant Design | 2015-06 | 2.7M | yes |
| react-bootstrap | 2014-01 | 1.1M | yes, dated |
| Chakra | 2020-11 | 1.3M | yes |
| Mantine | 2021-01 | 1.8M | yes |
| Radix Themes | 2023-06 | 735K | yes |
| Radix Primitives | 2020-12 | **50.9M** | no — headless |
| Base UI | 2024-12 | 9.7M | no — headless |

MUI is the only candidate that is both the longest-lived and a complete styled set. **Radix
Primitives' 50.9M is not a competing number**: it is headless, so it is an ingredient other kits
are built from (shadcn's legacy base among them), not an alternative to a styled library. The
fair comparison is Radix *Themes*, which is three years old, 10× less adopted, and ships no
async autocomplete, data grid or date picker — so the two largest hand-built surfaces would
stay hand-built.

Two facts worth not re-deriving. **shadcn/ui's current components sit on Base UI, not Radix**
(`ui.shadcn.com/docs/components/base/*`; the Radix set is behind a "Legacy Docs" link), and
**Base UI is built by MUI's own team** — so the headless and styled options here are one
strategy, not rival camps. A later move to shadcn would not be a repudiation of this decision.

## The constraint everyone expected to be decisive, and why it was not

#674's evidence comment named this as "the single most likely thing to disqualify a candidate":
colours must resolve through CSS custom properties, because that is what carries four palettes
× two themes and what `styles.test.ts` enforces.

It did not disqualify MUI, for two reasons found by spiking rather than by reading:

1. **The style guards parse `src/styles.css` with postcss.** They walk the stylesheet, not the
   rendered DOM. A library's own markup is invisible to them; only what we write in that file
   matters.
2. **MUI's colours are supplied by us.** `FarmThemeProvider` resolves the live tokens with
   `getComputedStyle` and hands MUI concrete strings, re-reading through a `MutationObserver` on
   `data-brand`/`data-theme`. `farmTokens.test.ts` proves all four palettes × both modes reach
   `palette.primary.main` distinctly, and that each produces derived hover/pressed states.

Concrete strings, not `var(--brand)`, is load-bearing: MUI derives those states with `alpha()`,
which parses the colour, and a `var()` reference throws at theme-creation time.

The same comment asserted that Tailwind + shadcn "replaces the token system rather than sitting
on it". **That is wrong and the record corrects it**: shadcn's default theming is CSS custom
properties. What Tailwind would displace is `styles.css`'s *layout* rules, not its colours —
which makes the deferred decision smaller than it was described as, not larger.

## The cost, measured rather than predicted

Precache budget, built at the head of each branch:

| | precache | JS gzip | CSS gzip | total gzip |
| --- | --- | --- | --- | --- |
| baseline, hand-rolled | 1312.45 KiB | 85.27 | 10.10 | **95.4** |
| Base UI, `Dialog` actually ported | 1365.15 KiB | 103.37 | 10.10 | 113.5 |
| MUI, provider only, zero components | 1397.79 KiB | 115.45 | 10.10 | 125.6 |
| **MUI + component kit** | **1632.68 KiB** | 186.98 | 10.10 | **197.1** |
| Radix Themes + the same kit | 2130.79 KiB | 132.08 | **92.27** | **224.4** |

The kit is the same on both sides — dialog, button, text field, select, table, tabs, tooltip,
badge, switch, alert — except that MUI's also includes `Autocomplete`, which Radix Themes has no
equivalent for. So MUI's number buys strictly more.

**MUI costs +320 KiB precache (+24%); Radix Themes costs +818 KiB.** The gap is CSS, not code:
Radix Themes ships one 730 KiB stylesheet carrying every accent colour in both modes whether or
not they are used, while MUI generates styles in the browser through Emotion. That inverts the
usual assumption that the lighter-feeling library is lighter, and it was measured rather than
guessed.

**The trade MUI makes is bytes for CPU, and that half is now measured.** Three PRODUCTION builds,
9 runs each, 6x CPU throttling, phone viewport, timed to the last Dashboard panel visible:

| build | wall | script | style recalc | layout |
| --- | --- | --- | --- | --- |
| base, no MUI | 917 ms | 107 ms | 15 ms | 26 ms |
| + theme provider | 921 ms | 119 ms | 15 ms | 26 ms |
| + MUI Dashboard (11 components) | 919 ms | 136 ms | 15 ms | 25 ms |

Three readings. **Wall time is flat** — 4 ms across 27 runs, because ~900 ms is network and API,
not rendering. **Style recalculation does not move at all**, which retires the specific concern
that Emotion's runtime CSS injection would be expensive; it is 15 ms in all three. **Script time
does rise** and is the honest cost: +12 ms for the provider, +29 ms with eleven components, at 6x
throttle — so roughly +5 ms on the real device.

The caveat is the slope, not the value: +27% script time for eleven components. It scales with MUI
usage, and this is a light dose. A screen using `Autocomplete` or a data grid needs its own
measurement before the slope is assumed flat.

An earlier figure taken against the Vite DEV server (1168 ms vs 1213 ms) is superseded by the table
above: a dev server carries HMR overhead and no minification, so it could not answer this.

This is a PWA for phones in sheds, so every slice that adopts an MUI component re-measures, and
a slice that adds weight without retiring hand-built code should be challenged.

The second accepted cost is the Material look. MUI renders as a Google app until deliberately
restyled; that work is the revamp's, not this record's.

## What the spike proved about migration cost

`spike/674-base-ui` ports `Dialog` onto Base UI: 338 → 240 lines, typecheck clean, **all style
guards green**, and 82 tests red across 16 files. Those failures are overwhelmingly assertions
about the *old mechanism* — `body.style.overflow === "hidden"`, `inert` on the container — where
the library reaches the same end differently (it aria-hides outside content and owns the stack).
A probe confirmed the behaviour itself was correct: focus landed on the first field, the
background left the accessibility tree.

**So the lesson is the cost model, and it generalises to MUI.** Porting a control is hours;
re-proving its earned accessibility guarantees is the work, and rewriting a guard to match a new
mechanism is exactly where an earned guarantee gets silently lost (AGENTS.md, "a wrong guard is
worse than no guard"). Several of those guarantees — `inert`, scroll lock — cannot be settled in
jsdom at all and need Playwright, which is what #501 already concluded.

Every migration slice therefore: states which issue's guarantee each rewritten test protects,
and verifies in a real browser anything jsdom cannot see.

## What is NOT decided here

- **Tailwind + shadcn/ui** — declined above, not deferred. No follow-up issue exists on purpose.
- **A chart library** (Recharts/visx, §8.1) — two charts exist and both work. Revisit at a third.
- **TanStack Query, React Hook Form + Zod, Dexie, an OpenAPI-generated client** — §8.1 also
  prescribes these and none is installed. Out of scope here; #50 owns the offline half.

## Consequences

- `specs/technical/tech_spec.md` §8.1 is amended to name MUI; the divergence is no longer silent.
- `web/README.md:11`'s "no CSS framework yet" is no longer true and is updated.
- MUI is a production dependency and therefore a new advisory surface under the #146 fail-closed
  audit gate.
- `FarmThemeProvider` must not render MUI's `CssBaseline`: its global resets would fight
  `styles.css` across all 124 components. Adopting it is a whole-app visual decision.

## Amendment (2026-09-14)

#822's design doc read this record's claims back against the code at `b0638e1` and four of them
moved. The corrections live here rather than in the design doc, so nobody re-derives them a third
time.

1. **"all 124 components"** is `find src -name "*.tsx" | wc -l` = 125 files, of which 56 are not
   tests. The number counted test files and was one out besides.
2. **The two consequences above landed in PR #860**, not in this record's own commit.
   `specs/technical/tech_spec.md` §8.1 and `web/README.md:11` were both still unamended at
   `b0638e1`; `c3a9a49` fixed them.
3. **"`FarmThemeProvider` must not render `CssBaseline`" still holds, and the reason changed.**
   #822 D6 took the decision to adopt it and #823 tried. It does not apply, and neither does any
   other MUI style: the production CSP is `style-src 'self'`
   (`src/Cluckwork.Api/Security/SecurityHeaders.cs`), so the browser refuses the stylesheet
   Emotion injects. Measured against the sim harness at 390: the `<style data-emotion>` tag is in
   the document, `html` computes `box-sizing: content-box` rather than CssBaseline's `border-box`,
   the page overflows to 419px in a 390px frame, and the console carries *"Applying inline style
   violates the following Content Security Policy directive 'style-src 'self''"*. **This blocks
   the whole migration, not one slice** — every `sx`, `styled()` and `styleOverrides` value in
   #826 to #836 arrives the same way. Nobody saw it before #823 because the app renders no MUI
   component yet, so the theme had nothing to emit. Adopting `CssBaseline` is one element in
   `FarmThemeProvider` once the CSP question is settled; settling it is its own decision, and the
   three candidate answers are `'unsafe-inline'` (a real relaxation), a per-request nonce threaded
   into `@emotion/cache` (which needs `index.html` templated per request rather than served as a
   static file), and a build-time extraction that emits MUI's CSS to a real stylesheet.
   The ten bare element selectors are a separate problem and #823 neutralises them regardless.
4. **"Inter's `opsz` axis is already installed, zero download cost"** is wrong in the way that
   matters. The files are in the package and the app does not load them: `main.tsx:3` imports
   `@fontsource-variable/inter`, whose `index.css` carries only `wght` faces. The `opsz` faces are
   a separate entry, and adopting them costs +118.9 KiB of precache (+24.1 KiB on the latin subset
   a phone fetches). So `font-variation-settings: "opsz" 32` on today's face does nothing. #835
   owns the swap and is charged against #825's ceiling.

## Amendment (2026-09-14): CSP — MUI's styles need a nonce, and the policy stays strict

**Issue:** #873 · **Found by:** #871, on the sim harness at 390px.

### The finding

This app ships `style-src 'self'` with no `'unsafe-inline'`. MUI styles through Emotion,
which injects a `<style>` element at runtime, and the browser refuses to parse it under that
policy. Nothing in the app reports it: the element sits in `<head>` with `sheet === null`,
React is happy, and the only complaint is one console line. Measured in #871 — `html` computed
`box-sizing: content-box` where `CssBaseline` sets `border-box`, and `/daily-entry` laid out
419px wide in a 390px frame. Every `sx`, `styled()` and `styleOverrides` value travels that
same path, so **no MUI styling reached the screen at all**. It stayed invisible until #871
only because no MUI component had rendered yet.

### The three options, and why the third won

| Option | What it costs |
| --- | --- |
| `'unsafe-inline'` on `style-src` | Reopens CSS injection, attribute-selector exfiltration included. A security regression this record would then have to own, to spare a library from carrying a nonce. |
| Build-time CSS extraction | Fights the decision above: the palette is read off the *document* at runtime so the four farm palettes (#149/#586) reach MUI with no per-palette configuration. Extraction wants the values at build time, which is the opposite. |
| **A per-response nonce** | One RNG draw per response, one templated document, one `CacheProvider`. The policy stays exactly as strict as it was. |

The owner chose the third (2026-09-14). `script-src` is untouched and must stay that way: the
pre-paint theme script was externalised in #144 precisely so scripts need no nonce.

### The invariant

**Every Emotion cache in this app carries the page's nonce.** There is one today —
`web/src/theme/FarmThemeProvider.tsx` builds it at module load from
`<meta name="csp-nonce">` and hands it to `CacheProvider`. A second cache created anywhere,
for any reason, is a second thing that must read the same meta; a cache without the nonce
injects styles the browser silently discards, and the screen looks *almost* right, which is
the worst way for this to fail.

A missing meta yields `nonce: undefined`, deliberately. That is the Vite dev server's document,
where no policy applies. In a production build it means the header's nonce reached nobody and
the styles are blocked — the fail-closed outcome. Do not add a fallback that loosens it.

### Two consequences worth knowing before you touch this

**`index.html` is no longer a static file.** `SpaShell` (`src/Cluckwork.Api/Hosting/`) reads it
once at boot, splits it at `<head>`, and writes the nonce meta into every response;
`SecurityHeaders.GetOrCreateNonce` is what both the document and the header read, so they cannot
disagree. It carries no `ETag` and no `Last-Modified` — a validator derived from the file would
tell a client its cached copy is fresh while the live header no longer admits that copy's nonce.
`#141`'s `no-cache` survives unchanged, and hashed `/assets/*` are untouched.

**For a client running the service worker, the nonce is per precache entry, not per request.**
`navigateFallback` (#142) answers navigations from the cached shell, online and offline alike, so
that client keeps one nonce until the worker updates. That is safe for the reason the offline
assertion in `tools/simulation/ui/specs/csp-nonce.spec.ts` checks rather than assumes: workbox
caches one whole `Response`, so the header and the body it stores were minted together and stay
together. What survives is "the nonce is unguessable and specific to this client's cached shell",
not "fresh on every navigation" — and the alternative, refusing to precache the shell, would
retire the offline guarantee #142 exists for.
