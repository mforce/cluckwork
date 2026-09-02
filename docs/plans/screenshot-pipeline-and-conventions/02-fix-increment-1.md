# Fix increment 1 — #660 is not met yet

Driver review of the first dispatch. **You flagged this yourself and were right to** — the runbook told you
to capture-and-commit, which is not what #660 asks for. That was a driver error; this increment fixes it.

## What #660 actually requires

Its acceptance, read again in full:

- [x] committed and embedded in `README.md`, **or** closed as won't-fix with the reason recorded — **met**
- [ ] **the image shows the panels the screen is for, not only the alarm state** — **NOT met**
- [ ] captured on a freshly reset fixture, and **`tools/simulation/ui/README.md`'s image count matches what ships** — **NOT met**

Driver-verified on the committed image: 1280×800, twelve `No entry` tiles filling the frame, `88 more
flocks`, and the 14-day trend and Stock panels cut off at the fold. And
`tools/simulation/ui/README.md:15` still reads *"Captures the root README's **three** images"* — four now
ship.

There is a third defect the driver found, not in the issue: **the README alt text describes content the
image does not contain** — *"a 14-day production trend, and stock on hand by grade"*. Those are below the
fold. Alt text is what a screen-reader user gets instead of the picture, so it must describe what is
actually in frame.

## The fix

Issue #660 offers three routes and names the first itself: *"give the capture a taller viewport (its own project
entry in `playwright.screenshots.config.ts`) so tiles + trend + stock + sales are all in frame."*

Take that one. Do **not** attempt the second route (a dedicated seed profile so the tiles show real
figures) — that is a fixture change well outside this slice.

## Files

- `tools/simulation/ui/playwright.screenshots.config.ts` — a taller viewport for the dashboard capture only.
- `tools/simulation/ui/specs-screenshots/screenshots.spec.ts` — only if the project split needs it.
- `docs/images/dashboard.png` — recaptured.
- `README.md` — alt text corrected to match the new frame.
- `tools/simulation/ui/README.md` — the image count.

**The other three images must not change.** Confirm with `git status --short` that only `dashboard.png` is
modified under `docs/images/`.

## Steps

1. **Taller viewport for the dashboard capture.** A separate project entry, so the other three keep
   1280×800 and stay byte-identical. Pick the height by looking: tall enough that the tile grid, `Last 14
   days`, `Stock` and the recent-sales list are all in frame, without so much empty space that the shot
   reads as a screenshot of a scrollbar. Say what you chose and why.
2. **Recapture.** The stack at `127.0.0.1:8081` is current with `main`. Run the screenshots from the main
   checkout as before, copy `dashboard.png` in, and leave the main checkout clean.
3. **Open the new image and look at it.** Then answer, in your report, in your own words: *does this show
   the panels the screen is for, or is it still only the alarm state?* You are the one who raised this; your
   judgement on whether it is fixed is the deliverable.
4. **Correct the README alt text** so it describes what is in the frame — following the house style of the
   other three, which describe content literally rather than captioning.
5. **`tools/simulation/ui/README.md`** — the count, and anything else there that assumes three.
6. **G1, G2, G3** unchanged from baseline; this slice still touches no application code.

## If it is still not right

**Say so and STOP.** A taller viewport may still be a wall of alarm tiles with panels beneath, because the
fixture has no entry for today by construction — every flock is legitimately in the alarm state. If that is
what you see, report it plainly. #660's own third option is *"decide the dashboard is not a README screen
after all and close this"*, and that is an owner decision, not yours or mine. Do not reach for the seed
profile to force a prettier picture.

## Commit

```bash
git add tools/simulation/ui/playwright.screenshots.config.ts \
        tools/simulation/ui/README.md docs/images/dashboard.png README.md \
        docs/plans/screenshot-pipeline-and-conventions/02-fix-increment-1.md
git commit -m "docs: capture the dashboard at a taller viewport so the whole screen is in frame (#660)"
git push origin chore/screenshot-pipeline-and-conventions
```

Then update the PR body: #660's acceptance, item by item, with what is now met and what is not.

## Report back

Commit SHA, the viewport you chose and why, your own judgement on whether criterion 2 is met, `git
status --short` in both checkouts, and G1/G2/G3.
