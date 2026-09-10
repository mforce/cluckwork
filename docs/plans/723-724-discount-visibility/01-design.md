# #723 + #724 — Discount visibility on the sales order screen and in history

One PR (owner's call, 2026-09-09). Epic #719, after #720 shipped as PR #734 (`cffed5e`).

**The mockups are the design authority.** `docs/images/discount-mockups/Main.png` (#723) and
`HistoryBadge.png` (#724), both at commit `d048390`, not present at HEAD — extract with
`git show d048390:docs/images/discount-mockups/<name>`. Where this document and a mockup disagree, the
mockup wins and the disagreement is recorded in §3.

## 0. What already shipped, read from the code at `cffed5e`

| Fact | Where |
|---|---|
| `OrderItem.listUnitPriceMinorUnits: number \| null`, required field | `web/src/api/cluckwork.ts:343` |
| `lineDiscount(item)` returns `none \| atList \| below{amountMinorUnits,percent} \| above` | `web/src/routes/SalesPage.tsx:130-142` |
| Items table already has **List price** and **Discount** columns, both `class="num"` | `SalesPage.tsx:949` (thead), `975-1015` (both branches) |
| A below-list cell renders `$12.00 · 11.1%` inside `<span class="discount">` | `SalesPage.tsx:1010-1014` |
| The List price cell renders the money value **plain** — no strikethrough today | `SalesPage.tsx:973-978` and `1003-1008` |
| `.discount` = `color: var(--warn); font-weight: 600` — colour only, no marker | `web/src/styles.css:1139` |
| `.badge` + `.badge-ok/-warn/-danger/-accent` exist on `--tint-*` tokens | `styles.css:1166-1183` |
| Order total is `<p><strong>` at `SalesPage.tsx:1041` — there is no `<tfoot>` to add a row to | `SalesPage.tsx:1041` |
| The Orders list table has columns Reference, Date, Customer, Status, Total, History, actions | `SalesPage.tsx:1393` (thead), `1396-1417` (rows) |
| `listPrice` / `discount` / `aboveList` / `noListPrice` exist in en, es, tl | `web/src/i18n/{en,es,tl}.ts:403-419 / 315-323 / 331-339` |
| Help string `salesListPrice` already describes List price + Discount + Above list + "No list price" | `web/src/i18n/en.ts:2816-2820` |
| **The order LIST endpoint already returns items** — `.Include(o => o.Items)`, same `ToResponse` | `SalesOrderRepository.cs` `ListAsync`, `SaleEndpoints.cs:229-252` |

**The last row is the decisive one: #724 needs no backend change.** `listOrders()` types as
`SalesOrder[]`, `items` is populated on the list route, and every item carries
`listUnitPriceMinorUnits`.

## 1. Simplicity ceiling

**Smallest viable implementation, in one sentence:** strike through the List price cell and add a
*Below list* chip on below-list rows, tint those rows, add one Discount paragraph above the existing
order total, and add one Discount column to the Orders table — every value derived from the
already-shipped `lineDiscount()` plus one new pure `orderDiscount()`.

**Complexity budget:** ~7 files — `SalesPage.tsx`, `styles.css`, `i18n/{en,es,tl}.ts`,
`SalesPage.test.tsx`, `docs/images/sales.png` (regenerated baseline).

**Non-goals:**

- No new `--tint-*` token. `styles.test.ts` would demand light and dark declarations across 4 brands ×
  2 modes; `.badge-warn` already carries `--tint-warn`.
- No new API field, query or endpoint.
- No `<tfoot>`. The Discount paragraph is a sibling `<p>` above the order-total `<p>`.
- No change to `lineDiscount()`'s shape or to any #720 wire contract.
- No discount **reason** anywhere (that is #721 — see §2).

## 2. Scope-ownership map

| Surface | Owning slice | Lands first | Forward-compat carried by |
|---|---|---|---|
| `ListUnitPriceMinorUnits`, its API projection, the stale-price guard | **#720** | shipped `cffed5e` | n/a |
| List price + Discount **columns**, the live typing hint, `.discount` | **#720** | shipped `cffed5e` | n/a |
| Strikethrough, *Below list* / *No list price* chips, row tint, **Discount total** | **#723** | this PR | this PR |
| **Discount column** on the Orders table | **#724** | this PR, after #723's terminology | this PR reuses #720's terms |
| Discount **reason** (captured at confirm, shown as the badge's `title`) | **#721** | not started | **#721** |
| Audit payload carrying the price change | **#722** | not started | #722 |
| Report/export totals, customer history, ceiling+approval, alert entry | #725/#726/#727/#728 | later | those slices |

**Consequence for #724:** its scope line *"The discount reason from #721 shown on order detail beside
it"* is **not implementable in this PR**. The mockup renders that reason as the badge's `title`
attribute, which is #721's to fill. Owner decision 2026-09-09: **amend #724 and ship without it.**

## 3. Conflict table

| # | What the issue/mockup requires | What the repo says | Resolution |
|---|---|---|---|
| C1 | #723: "List price shown **struck through** beside the actual price" | #734 gave List price its own column, rendered plain. `Main.png` strikes through **the List price cell** on below-list rows — not a duplicate beside the unit price. | **Settled by the mockup.** Strike through the List price cell on `kind === "below"`. No duplicated number. |
| C2 | Percent basis for the order-level figure. | Owner decision 2026-09-09: **percent is off list, over comparable lines only** — `discount ÷ Σ(list × qty)` for every line that HAS a list price. The mockups' printed percentages (9.0%, 14.0%) are `÷ (total + discount)` and are **illustrative of layout, not of arithmetic**; the owner confirmed the mockups answer the List price column's appearance, not the calculation. The same order reads **9.7%**. | **Settled.** Comparable lines only. The mockups' numbers are not a specification and are not to be reproduced. |
| C3 | `Main.png` renders a no-list-price line's Discount cell as "Not measurable", with a "No list price" chip in the Product cell. | #720 shipped `noListPrice` = "No list price" **in the Discount cell**, and `salesListPrice` help prose documents that wording in all three locales. | **Settled, owner 2026-09-09: keep the shipped wording.** The Discount cell stays "No list price"; the Product-cell chip is what this slice adds. Changing shipped, glossary-backed copy would need help edits in three locales for no behavioural gain, and #688 forbids a locale disagreeing with itself about one control. |
| C4 | Row marking, colour not the only signal | `styles.caps.test.ts` holds `text-transform: uppercase` to an equality set of exactly `.more-group-label`, `.nav-group-label`; `styles.elevation.test.ts` holds drop shadows to an equality set. | New CSS is **background tint + strikethrough only**. No shadow, no uppercase, no new token. |
| C5 | #724 badge text must agree with #723 per locale (#688) | `discount` exists in en/es/tl with glossary entries; `catalogParity` compares key sets only and would not catch a divergent word. | Reuse the existing `discount` string. The new *Below list* chip needs a new key in all three locales, worded from each locale's own `listPrice` label. |
| C6 | Repo rule: user-visible behaviour updates GLOSSARY + Help + in-app glossary | `salesListPrice` exists and names List price / Discount / Above list. | Extend `salesListPrice` in all three locales to name the row marking, the Discount total and the Orders-table Discount column, using each locale's own label words (#688). Glossary term *Discount* does not change meaning — no new term. |

## 4. Invariants

Named once. Never renumbered.

| ID | Invariant | Enforcement sites |
|---|---|---|
| **INV-1** | A line whose `listUnitPriceMinorUnits` is `null` reads as *No list price* in its Discount cell and carries the *No list price* chip. Never 0% off, never at-list, never discounted. | `lineDiscount()` `kind:"none"`; both render branches |
| **INV-2** | The order's discount amount is the sum of **below-list line amounts only**. An above-list line never nets against it, and the amount is never negative. | `orderDiscount()` |
| **INV-3** | An order in which **no line** has a list price reads as **Unknown**, never as a clean zero and never as at-list — this is the pre-#720 order. An order in which **some** line has no list price reports its figure as covering only part of the order, on any surface that shows no line detail. | `orderDiscount()` `kind:"unknown"` and its `partial` flag; the Orders-table cell; the Discount paragraph |
| **INV-4** | Colour is never the only signal. A discounted row carries the *Below list* chip and a struck-through list price; the Orders-table cell carries text. All survive greyscale. | the chip, the strikethrough, the Discount column's text |
| **INV-5** | No CSS this PR adds casts a drop shadow, sets `text-transform: uppercase`, or declares a new brand token. | `styles.elevation.test.ts`, `styles.caps.test.ts`, `styles.test.ts` |
| **INV-6** | The three locales use one word per control, and help prose naming a control uses that control's own label in that locale. | `i18n/{en,es,tl}.ts`; review-only per #688 — nothing enforces it |
| **INV-7** | Every new money or numeric cell carries `class="num"`. | `styles.num.test.ts`, `SalesPage.test.tsx` |

## 5. Design

### 5.1 `orderDiscount()` — one new pure helper beside `lineDiscount()`

Settled by the owner's C2 answer: **off list, comparable lines only.** A line is *comparable* when
`listUnitPriceMinorUnits !== null` — which includes at-list and **above-list** lines, both of which have
a list price and therefore belong in the denominator.

```
unknown                    — NO line is comparable. Renders "Unknown".              (INV-3)
atList  { partial }        — comparable lines exist, none below list.
below   { amountMinorUnits, percent, partial }
          amountMinorUnits = Σ below-list line amounts                              (INV-2)
          percent          = amountMinorUnits ÷ Σ(list × qty) over COMPARABLE lines
          partial          = at least one line is NOT comparable                    (INV-3)
```

`partial` sits on **both** populated variants, not only on `below`. The design contrarian
(2026-09-09, `gpt-5.6-sol`) found both defects in the first draft and both are fixed here:

1. The first draft excluded above-list lines from the denominator while INV-3 called every non-null-list
   line comparable. An order of one $100-at-$110 line and one $100-at-$90 line reported **10%** where
   the invariant requires **5%**. Above-list lines are now in the denominator, never in the numerator.
2. The first draft carried `partial` only on the `below` variant, so an order of one no-list-price line
   plus one at-list line had **no representable state** — it returned a bare `atList` and read as a
   clean zero. `atList` now carries `partial` too.

Worked example, the `Main.png` order, under this rule: comparable list value is
`240×$0.45 + 8×$16.50 + 20×$5.40 = $348.00`; discount is `$12.00 + $21.60 = $33.60`; percent is
**9.7%**, `partial` is **true** (the Cracked Eggs line has no list price). The mockup prints 9.0%
because it divides by `total + discount`; per C2 that number is not the specification.

### 5.2 Order screen (#723) — `Main.png`

- **Row tint.** `<tr className={discount.kind === "below" ? "discounted" : undefined}>`;
  `.discounted td { background: var(--tint-warn); }`. One declaration, existing token.
- **Strikethrough.** On `kind === "below"`, the List price cell renders inside `<s>` (or a
  `.struck` class). C1.
- **Chips in the Product cell.** *Below list* on `kind === "below"`; *No list price* on
  `kind === "none"`. Reuse `.badge` + an existing variant; new i18n keys.
- **Discount paragraph.** Immediately above the order-total `<p>` at `SalesPage.tsx:1041`, rendered
  only when `orderDiscount().kind === "below"`. Text follows the mockup:
  `Discount: −$33.60 · 9.7% of list` — 9.7%, not the mockup's 9.0%: §5.1's denominator is the
  comparable lines only, and C2 records that the mockup's percentages are layout illustrations.
- **Inline-edit branch.** The row keeps whatever marking the **server** line carries; the typed price
  is not yet the line's price, and #720's live hint below the form already covers the in-flight value.

### 5.3 Orders table (#724) — `HistoryBadge.png`

- **One new column**, header `Discount`, between **Status** and **Total**.
- `below` → `<span class="badge badge-warn">` reading `9.0% · $33.60` — **percent leads**, because a
  reviewer scanning a month reads for outliers.
- `atList` and not `partial` → em dash.
- `unknown` → the word **Unknown**, plain text, not a badge (INV-3).
- Any `partial` order → the cell also carries a muted text marker saying the figure covers part of the
  order. This is the surface INV-3 is really about: the Orders table shows no line detail, so without
  the marker an order with one unpriced line reads as a clean measured zero. New i18n key, all three
  locales.
- The badge's `title` is where #721's reason will go. Not this PR.
- **"Same treatment on order detail"** is satisfied by §5.2's Discount paragraph — the mockup shows no
  second badge on the order panel.

### 5.4 Call-site counts before styling (#662)

Run at `cffed5e`:

| Selector | Call sites in `web/src/**/*.tsx` | Decision |
|---|---|---|
| `.discount` | **5** | reused, not restyled |
| `.badge` / `.badge-warn` | in use across screens; `badge-warn` at `styles.css:1181` | reused as-is |
| `.discounted` | **0** | new class — expected for new CSS, stated as a decision |
| `.struck` (if used rather than `<s>`) | **0** | new class — same |

### 5.5 Screenshots (#662)

`screenshots.spec.ts:193` captures `docs/images/sales.png` into a **committed** path, and that baseline
moves. This PR's purpose is visual, so it owes a 1:1 before/after pair captured from a stack rebuilt at
the head under review, plus the regenerated baseline. Not downscaled.

## 6. What the design predicts about itself

The leg to test rather than record: **greyscale legibility of `--tint-warn` as a full-row wash.** A tint
chosen for a badge pill may be too faint across a row, in which case the chip and the strikethrough are
carrying INV-4 alone. Check against a rendered row, not on paper.

## 7. Risk class

**Medium.** One screen; no migration; no authorization, tenancy, transaction or concurrency surface;
fully reversible. Not low: derived **money** arithmetic across three locales, an accessibility
acceptance criterion, and three whole-stylesheet equality guards under the CSS.
