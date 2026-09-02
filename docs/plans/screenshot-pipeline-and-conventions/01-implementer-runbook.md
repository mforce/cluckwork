# Runbook — screenshot pipeline + the conventions #651/#652 earned (#660, #662, #663, #664)

You are an autonomous coding agent with full tools in a git worktree of the Cluckwork repo
(.NET 10 API, React 19 + Vite SPA in `web/`). Execute this top to bottom. You do everything except the
merge: edit, test, commit, push, open the PR.

**Four issues, one slice.** They are all about the same thing — the screenshot pipeline produces what the
repo needs, and the conventions the last slice paid for get written down.

- **#663** — `docs/images/dashboard.png` is captured by the spec but never committed.
- **#660** — the README has no dashboard screenshot.
- **#664** — #651/#652 were only ever checked in light + default palette.
- **#662** — three conventions from #651/#652 belong in `AGENTS.md`, plus a runbook note.

Read all four issue bodies before you start: `gh issue view 660`, `662`, `663`, `664`.

## Rules

- Run commands exactly as given; do not invent flags.
- **Do not touch:** `web/src/**` (this slice changes no application code), `web/vite.config.ts`,
  any existing test under `web/src/`, `docs/decisions/**`.
- **Files you may create or edit:** `AGENTS.md`, `README.md`, `docs/images/dashboard.png`,
  `docs/runbooks/simulation-fixture-on-a-dev-database.md`,
  `tools/simulation/ui/specs-screenshots/**`, `tools/simulation/ui/playwright.screenshots.config.ts`,
  `tools/simulation/ui/package.json` (a script entry only), `.gitignore`, and this plan directory.
  Anything else: STOP and report.
- Work only on this branch. Never commit to `main`.
- **If you run the same command more than about five times without progress, STOP and report.**

## The stack

A sim stack is already running at `http://127.0.0.1:8081` and **its tree matches `main`** — verified by
the driver with `git diff --stat c0c93ec4 main -- web/ tools/`, which is empty (`c0c93ec4` is the branch
head that was squash-merged as `28db4c75`). So captures from it are valid for `main`.

**Do not run `tools/simulation/reset.sh`.** It would rebuild the stack from this worktree, which needs
`.env.sim` and `.sim-cast.json` copied in first (they are gitignored and live only in the main checkout)
and takes ~20 minutes. You do not need it — the stack is current.

`tools/simulation/ui/` in this worktree has **no `node_modules`**. Run the capture commands from the main
checkout instead: `cd /home/mforce/dev/cluckwork/tools/simulation/ui`. The specs drive the running stack
over HTTP, so which checkout you run them from does not change what they capture — but the SPEC FILES you
edit must be the ones in THIS worktree, so copy your edited spec across when you run it, or run with an
explicit `--config` pointing at this worktree's file. Say in your report which you did.

## Gate commands

| ID | Gate | Command | Baseline on `main` |
|---|---|---|---|
| G1 | web build | `npm run build` in `web/` | clean — driver-verified |
| G2 | web tests | `npm run test:coverage` in `web/` | `Test Files 110 passed (110)` / `Tests 2411 passed (2411)`; statements 90.77 branches 86.04 functions 85.84 lines 93.67 |
| G3 | schema docs | `bash tools/schema-docs/generate.sh --check` | clean — this slice changes no migration, so it must stay clean |

G1 and G2 should be untouched by this slice — you are changing no application code. **If either moves,
STOP and report**, because that means you edited something you should not have.

---

# INCREMENT 1 — commit the dashboard image and put it in the README (#663, #660)

## 1a. Capture

Before capturing, assert the main checkout's `docs/images/` is clean:

```bash
cd /home/mforce/dev/cluckwork && git status --porcelain docs/images/
```

If that prints anything, **abort and report** — someone has an in-progress edit there and the capture must
not run over it.

From the main checkout, capture the current four images:

```bash
cd /home/mforce/dev/cluckwork/tools/simulation/ui && npm run screenshots
```

Copy the produced `docs/images/dashboard.png` into THIS worktree, then restore the main checkout by
name — only the four generated files, never the whole directory:

```bash
cd /home/mforce/dev/cluckwork && git checkout -- docs/images/daily-entry.png docs/images/dashboard.png docs/images/reports.png docs/images/sales.png
```

Delete any untracked file the run created. **Report the main checkout's `git status --porcelain` at the
end to prove you left it clean.**

## 1b. Commit the image and reference it

- `git add docs/images/dashboard.png` in this worktree — it joins the three already tracked.
- Add it to `README.md`. Match the existing house style exactly: the other three use a long, literal
  `![...]` alt describing what is *in* the image, not a caption. Read lines 12, 90 and 95 of `README.md`
  and follow that shape. Place it where a reader first needs to see what the app looks like — the
  dashboard is the landing screen, so it belongs at or above the daily-entry image; use your judgement
  and say why in your report.
- In `tools/simulation/ui/specs-screenshots/screenshots.spec.ts`, the staleness-contract comment should
  now say all four captures are committed. Update it so the next reader is not left wondering whether the
  fourth is deliberate.

## 1c. Commit

```bash
git add docs/images/dashboard.png README.md tools/simulation/ui/specs-screenshots/screenshots.spec.ts
git commit -m "docs: commit the dashboard screenshot and show it in the README (#660, #663)"
```

---

# INCREMENT 2 — capture the palette × theme matrix (#664)

Issue #664 asks for a **visual check** of #651/#652 in night mode and the three non-default farm palettes. It
does **not** ask for those images to be committed — 8 states × several screens would bloat the repo for
no ongoing benefit.

## 2a. A separate, uncommitted capture

Add `tools/simulation/ui/specs-screenshots/palettes.spec.ts` plus an npm script (`screenshots:palettes`)
and, if the existing config cannot be reused cleanly, a config beside it. It captures the dashboard, one
list screen (sales) and daily entry across **4 palettes × 2 themes = 8 combinations**, writing into a
**gitignored** directory — `tools/simulation/ui/out-palettes/` — not `docs/images/`. Add that path to
`.gitignore`.

**Set the palette and theme by setting the attributes directly on the document element**, e.g.
`page.evaluate(...)` setting `data-brand` and `data-theme` on `document.documentElement`, rather than
driving the Settings screen. Two reasons, and put them in a comment: it exercises exactly the CSS
attribute selectors the palettes are defined with (`:root[data-brand="forest"][data-theme="dark"]`), and
it does not persist a farm setting that every other spec then inherits. The default palette carries **no**
`data-brand` attribute — read `web/src/styles.test.ts`'s `attrFor` helper for the convention.

Name the files so a human can sort them: `<screen>-<palette>-<theme>.png`.

## 2b. Run it, and LOOK at the output

Run the capture, then **open the images and actually look at them**. This is the point of the issue; a
green run is not the deliverable. Check specifically, on each of the eight:

- Table headers are legible — they moved to `--ink` at 0.8rem in #652.
- Badges read as sentence case and their tinted pills still separate from the row.
- Panels read as panels without their old shadow.
- Nothing has become invisible against its background.

**Report what you saw, per combination, in your own words.** If something looks wrong, say so and STOP
rather than fixing it — a visual defect here is a finding for the driver to route, not part of this slice.

## 2c. Commit

```bash
git add tools/simulation/ui/ .gitignore
git commit -m "test(sim): capture the palette and theme matrix for visual review (#664)"
```

---

# INCREMENT 3 — the conventions (#662)

Read `gh issue view 662` in full; it carries the proposed wording and the reasoning behind each rule.

## 3a. `AGENTS.md`

Add the three rules. **Match the file's own form exactly** — read the surrounding rules first: each is
**one paragraph**, bolded lead sentence, the reasoning inline, and a `→ docs/decisions/...` link only
where a decision record exists. These three have no decision record, so they carry no link; they are
conventions earned by a defect, and the issue number belongs in the text.

1. **Count a selector's call sites before styling it** — near the guard-writing section. Three selectors
   named in #651/#652 had no consumers; two were caught, one shipped as styled dead code.
2. **Removing a presentational transform makes every string it transformed a caller** — in the i18n
   or data-correctness area, whichever fits the file's structure better. This is the #394 caller rule
   applied to text.
3. **A PR whose purpose is visual attaches a 1:1 before/after comparison, captured from a stack rebuilt
   at the head under review** — beside the SPA E2E rules. Both mechanics matter: 1:1 because downscaling
   destroys hairlines and low-alpha shadows, and rebuilt because a long-running stack serves the bytes it
   was built from.

**Do not restructure the file** and do not touch rules you are not adding.

## 3b. The runbook note

In `docs/runbooks/simulation-fixture-on-a-dev-database.md`, record what breaks when `reset.sh` is run
from a git worktree: it fails with `tools/simulation/.env.sim not found` because that file and
`.sim-cast.json` are gitignored and exist only in the main checkout — copy both in first. Add that
`tools/simulation/ui/` has no `node_modules` in a worktree, and that the capture specs drive the stack
over HTTP so they can be run from the main checkout against a worktree-built stack.

## 3c. Commit

```bash
git add AGENTS.md docs/runbooks/simulation-fixture-on-a-dev-database.md
git commit -m "docs: record the conventions #651 and #652 earned (#662)"
```

---

# FINISH

Run **G1**, **G2** and **G3**. All three must match the baseline — this slice changes no application
code, so a moved test count means you edited something outside the allow-list.

```bash
git push -u origin chore/screenshot-pipeline-and-conventions
```

Open the PR with `gh pr create`. Title exactly:

```text
docs(sim): commit the dashboard screenshot, capture the palette matrix, and record the #651/#652 conventions (#660, #662, #663, #664)
```

Body: what changed per issue; the palette-matrix observations from 2b in full; G1/G2/G3 output; and a
line stating that `docs/images/dashboard.png` is now committed and the palette captures deliberately are
not, with the reason.

Do NOT merge. Do NOT mark ready or draft either way.

## Report back

Branch, every commit SHA, `git status --short` here AND in the main checkout, G1/G2/G3 output, the
per-combination visual observations, the PR URL, and anything here that turned out to be wrong.
