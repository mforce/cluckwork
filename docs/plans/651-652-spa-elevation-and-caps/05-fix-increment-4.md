# Fix increment 4 — two badge strings that relied on CSS to capitalise them

**The first genuine PRODUCT defect of this slice.** Every earlier round found defects in the guards or
the documentation; this one is visible to a user on the dashboard.

Found by the driver comparing before/after screenshots of the real built SPA — not by any review seat,
because it is not visible in a selector sweep or a stylesheet walk. #652's own acceptance criterion
named this exact risk: *"verify nothing relied on CSS to capitalise"*. Two strings did.

## The finding

`.badge` used to apply `text-transform: uppercase`. Two badge strings were therefore authored in lower
case at source, because the stylesheet made them read as capitals. With the transform gone they render
exactly as written, which reads as a typo rather than as sentence case.

| Key | Rendered by | en | es | tl | Was | Now |
|---|---|---|---|---|---|---|
| `noEntryBadge` | `web/src/routes/Dashboard.tsx:150`, `<span className="badge badge-warn">` | `no entry` | `sin registro` | `walang tala` | `NO ENTRY` | `no entry` |
| `saleableYesBadge` | `web/src/routes/GradesPage.tsx:270`, `<span className="badge badge-ok">` | `yes` | `sí` | `oo` | `YES` | `yes` |

**The set is complete, not a sample.** Every `*Badge` key in every locale was enumerated: four per
locale, and exactly these two start lower case in each. `editingDraftBadge` and `disabledBadge` are
already capitalised and render correctly — the daily-entry screenshot confirms `Editing draft`.

Eight other lower-case-initial strings were checked and are NOT affected: they render through `.muted`,
`.sr-only`, `.sidebar-version` or plain labels, none of which ever uppercased.

## Files

- `web/src/i18n/en.ts`, `web/src/i18n/es.ts`, `web/src/i18n/tl.ts`
- `web/src/i18n/badgeCase.test.ts` — new guard, **PROTECTED**
- `web/src/routes/Dashboard.test.tsx` and `web/src/routes/HelpPage.test.tsx` — two assertions pin the old
  text and must move with it
- `web/src/styles.css` is NOT touched.

## Step 1 — the strings

**`noEntryBadge`** — and, because the Help page and glossary quote this badge by name, its three quoted
copies move with it. AGENTS.md treats a missing doc update like a missing test, and here the doc quotes
the exact string.

| File | Line | From | To |
|---|---|---|---|
| `en.ts` | 626 | `noEntryBadge: "no entry",` | `noEntryBadge: "No entry",` |
| `en.ts` | ~2404 | `A tile marked <strong>no entry</strong> is` | `A tile marked <strong>No entry</strong> is` |
| `en.ts` | ~3186 | `"no entry" is the alarm state` | `"No entry" is the alarm state` |
| `es.ts` | 517 | `noEntryBadge: "sin registro",` | `noEntryBadge: "Sin registro",` |
| `es.ts` | ~1878 | `marcada <strong>sin registro</strong>` | `marcada <strong>Sin registro</strong>` |
| `es.ts` | ~2692 | `"sin registro" es el estado de alarma` | `"Sin registro" es el estado de alarma` |
| `tl.ts` | 539 | `noEntryBadge: "walang tala",` | `noEntryBadge: "Walang tala",` |
| `tl.ts` | ~1951 | `may <strong>walang tala</strong>` | `may <strong>Walang tala</strong>` |
| `tl.ts` | ~2789 | `ang "walang tala" ang alarm state` | `ang "Walang tala" ang alarm state` |

**Do NOT change `tileLinkLabelMissing`** (`en.ts:635` and its siblings). That is a screen-reader
sentence — `"{{flock}}: no entry yet, open today's entry"` — where the phrase is prose mid-sentence, not
the badge label. Capitalising it would be wrong.

**`saleableYesBadge`** — no Help or glossary copy quotes it (the `<strong>saleable</strong>` in the Help
text quotes the column label `saleableLabel`, which is unchanged).

| File | From | To |
|---|---|---|
| `en.ts` | `saleableYesBadge: "yes",` | `saleableYesBadge: "Yes",` |
| `es.ts` | `saleableYesBadge: "sí",` | `saleableYesBadge: "Sí",` |
| `tl.ts` | `saleableYesBadge: "oo",` | `saleableYesBadge: "Oo",` |

Mind the accent: the Spanish value is `Sí`, not `Si`.

## Step 2 — the guard

Create `web/src/i18n/badgeCase.test.ts`. **PROTECTED — transcribe or STOP.**

```ts
import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

// #652 removed `text-transform: uppercase` from `.badge`. Two badge strings had
// been authored in lower case BECAUSE the stylesheet capitalised them, so they
// began rendering as "no entry" and "yes" — which reads as a typo, not as
// sentence case. A user saw it on the dashboard; no selector sweep or
// stylesheet walk can, because the defect is in the text, not the CSS.
//
// The convention this pins: a key ending in `Badge` is rendered inside a
// `.badge` pill, and a pill's label starts with a capital. Enumerated from the
// locale files rather than listed here, so a new badge string is covered the
// day it is added rather than the day someone remembers this test.
const LOCALES = ["en", "es", "tl"] as const;

function badgeStrings(locale: string): [string, string][] {
  const src = readFileSync(resolve(process.cwd(), `src/i18n/${locale}.ts`), "utf8");
  const out: [string, string][] = [];
  const re = /^\s*([A-Za-z0-9_]*Badge)\s*:\s*"([^"]*)"/gm;
  for (let m = re.exec(src); m !== null; m = re.exec(src)) out.push([m[1], m[2]]);
  return out;
}

describe.each(LOCALES)("%s: badge labels are capitalised", (locale) => {
  const entries = badgeStrings(locale);

  it("finds badge strings at all — a regex that matches nothing would pass every assertion below", () => {
    expect(entries.length).toBeGreaterThanOrEqual(4);
  });

  it.each(entries)("%s starts with a capital", (_key, value) => {
    // Interpolation placeholders and digits are legitimate openers; a lower-case
    // letter is the defect this guard exists for.
    expect(value[0]).not.toMatch(/\p{Ll}/u);
  });
});
```

The first test is deliberate: a regex that stopped matching would leave `it.each([])` asserting nothing
and the file would pass while guarding nothing. That is the shape of false green this slice has already
hit twice.

## Step 3 — the two pinned assertions

- `web/src/routes/Dashboard.test.tsx:115` — `getByText("no entry")` becomes `getByText("No entry")`.
- `web/src/routes/HelpPage.test.tsx:1101` — `getByText("no entry", { selector: "strong" })` becomes
  `getByText("No entry", { selector: "strong" })`.
- `web/src/routes/GradesPage.test.tsx:64` — `getByText("yes")` becomes `getByText("Yes")`.
- `web/src/routes/GradesPage.test.tsx:459` — `queryByText("yes")` becomes `queryByText("Yes")`.

**Amendment, added after the first dispatch stopped on it.** The first two were all the driver's caller
ledger listed, and the ledger was wrong: it was built by grepping for the i18n KEY names plus the single
literal `"no entry"`, and never grepped the literal `"yes"`. The implementer hit the miss at G2, stopped
rather than fixing an unlisted file, and reported it — which is exactly the behaviour the runbook asks
for, and the reason the defect surfaced as a question instead of as a silent scope creep.

The corrected method, which is what found the fourth site: **grep every VALUE of every changed key, in
every locale, across every test and spec file** — not the key names, and not one representative literal.
A key name appears where the string is produced; the literal appears where it is asserted, and those are
different files.

Line 459 is the subtler of the two. It sits inside a `withOverride("grades", "saleableYesBadge",
"YES-MARKER", ...)` block and asserts the DEFAULT label is absent while the override is active. It does
not fail after the rename — `queryByText` takes an exact, case-sensitive string, so `"yes"` simply stops
matching anything — but it stops testing what it claims: it would pass even if the override did nothing.
A test that passes for the wrong reason is the `false-green` class this slice has already paid for
twice, so it moves with the value rather than being left green.

Change only the expected string. **If either test then fails for any other reason, STOP and report** —
that means something else depended on the old text.

## Step 4 — mutation rows

| # | Mutation | Must go | On which assertion |
|---|---|---|---|
| M27 | revert `en.ts` `noEntryBadge` to `"no entry"` | RED | `en: badge labels are capitalised > noEntryBadge starts with a capital` |
| M28 | revert `tl.ts` `saleableYesBadge` to `"oo"` | RED | the `tl` case of the same assertion |
| M29 | change `es.ts` `saleableLabel` to lower case (it already is) — i.e. confirm a NON-`Badge` lower-case string does not trip the guard | **GREEN** | proves the guard is scoped to the badge convention and is not just "no lower-case strings anywhere" |
| M30 | break the regex: change `Badge` to `Badgez` in the guard's own pattern | RED | `finds badge strings at all` — proves the empty-match false green is closed |

M30 mutates the test file rather than the product; restore it exactly.

## Step 5 — gates and commit

**G1**, then **G2**. The suite count rises by the new guard's cases.

```bash
git add web/src/i18n/ web/src/routes/Dashboard.test.tsx web/src/routes/HelpPage.test.tsx web/src/i18n/badgeCase.test.ts docs/plans/651-652-spa-elevation-and-caps/
git commit -m "fix(web): capitalise the two badge labels that relied on CSS to shout (#652)"
git push origin feat/651-652-spa-elevation-and-caps
```

Do NOT reply on GitHub, do NOT edit the PR description, do NOT merge.

## Report back

Commit SHA, `git status --short`, G1 and G2 output, the M27-M30 table as OBSERVED, and the new suite
totals.
