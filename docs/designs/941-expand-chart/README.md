# #941 — the expanded Lay rate chart: concept C

The direction the owner chose on 2026-09-24, kept here as the record the
implementation was built against. `expand-chart-direction-lab.html` is the
mockup lab: a self-contained file with live switchers for concept, range,
theme and locale, and `shoot-c2.mjs` beside it regenerates every render at
1:1 through Playwright.

Concepts A and B are kept in the lab as history, with the forced dark tokens
they were drawn with. Only C is the chosen one, and the lab sheet marks it.

## What the owner decided

- **Concept C, made scrollable.** An overview map of the whole range with a
  box marking the days in view, above a daily window that is a native
  horizontal scroll region. Scrolling moves the box; dragging or pressing the
  map scrolls the window. Dragging the box is desktop only; a phone gets
  tap-to-jump plus swipe.
- **It follows the app theme**, light in light mode and dark in dark, rather
  than forcing a dark lightbox.
- **Bars are always 22px**, never stretched. The newest day sits at the right
  edge, and a range narrower than the window leaves its empty space on the
  left.
- The card keeps #940's 7/14-day limit; the month and the quarter live here.

## Where the lab and the shipped chart deliberately differ

The lab is a mockup, and building it surfaced four things it got wrong. Each
is fixed in the implementation and pinned by a test.

- **At 30 days the lab stretched its slots to about 29px.** The owner's rule
  replaces that: 22px at every range, packed to the right
  (`ExpandedLayRate.styles.test.ts`).
- **The lab decided "fits" from `scrollWidth`.** A strip that fits still
  reports a few px of it, because the date rule's end label overhangs its
  slot, which offset the box and lit an edge cue on a chart clipping nothing.
  The chart decides it from layout instead (`lib/dayWindow.ts`).
- **The lab's edge cues live inside the scroll region**, so they are
  positioned against the scrolled content rather than its edges — the pale
  band visible down the middle of `C2-1280-90-mid-light.png`. The chart puts
  them outside it (`ExpandedLayRate.test.tsx`).
- **The lab keeps the card's 9px week jog.** Over a quarter that is more than
  100px the slot arithmetic does not account for, so the shipped strip draws
  the week hairline without the margin.

## Regenerating the renders

The `C2-*.png` frames are attached to issue #941 rather than committed: they
are reproducible from this file by name, and a design record does not need
half a megabyte of binaries in git history.

```bash
node shoot-c2.mjs   # needs playwright; writes C2-*.png beside this file
```
