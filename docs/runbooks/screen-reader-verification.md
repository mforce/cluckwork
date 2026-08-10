# Runbook: verifying an announcement with a real screen reader

**Issue:** #501 · **Applies to:** any change to a live region or the modal `inert` sweep · **Requires:** a Windows box with NVDA or JAWS, and/or a Mac with VoiceOver

**When to run this:** after any change to a live region, an `aria-live`
attribute, a `role="alert"`/`role="status"`, or to the modal `inert` sweep in
`web/src/components/Dialog.tsx`. First run: #501, verifying #485/#499.

**Why it is a runbook and not a test.** Nothing in this repository can observe
an announcement.

- **jsdom** (the `web/` unit suite) implements neither live regions nor the
  `inert` IDL. Verified directly rather than assumed: `'inert' in element` is
  `false`, and a click passes straight through an `inert` subtree. Those tests
  exercise the app's own JS bookkeeping and nothing the browser does with it.
- **Playwright** runs a real browser, so `inert` is real there — but its own
  accessibility surface does not model it (`ariaSnapshot()` still lists an
  inert node; `isVisible()` and `isEnabled()` still return `true`). Only CDP
  `Accessibility.getFullAXTree` agrees with the spec, which is what
  `tools/simulation/ui/specs/a11y-live-regions.spec.ts` uses.
- Even there, **presence in the accessibility tree is the precondition for an
  utterance, not the utterance.** Whether a screen reader speaks, and speaks
  once, is what this runbook is for.

---

## 1. Set up

```bash
# A production-shaped build, because the announcers ship in the SPA bundle.
bash tools/simulation/reset.sh          # stack on http://127.0.0.1:8081
```

Sign-in credentials come from the git-ignored `tools/simulation/.sim-cast.json`
— never hardcode one into a note or a spec.

Run each scenario on **at least two** of these pairings. NVDA + Firefox and
VoiceOver + Safari are the highest-value pair: they disagree most, and the
Safari/VoiceOver behaviour below is the one that shaped #499's design.

| AT | Browser | Notes |
|---|---|---|
| NVDA | Firefox | The most common desktop pairing. |
| NVDA | Chrome | Same engine as the automated spec, different AT plumbing. |
| JAWS | Chrome | Verbosity defaults differ; note the profile used. |
| VoiceOver | Safari | **The reason `aria-live` regions here are inserted EMPTY.** Per W3C ARIA22, a live region must carry its role/attributes *before* text lands in it; a region populated at insertion time is dropped by VoiceOver. |

Record the AT build number and browser version — a behaviour that changes
between AT releases is exactly the kind of thing this table exists to catch.

**Turn speech logging on** if the AT offers it (NVDA: Speech Viewer; JAWS:
Speech History). A transcript settles "did it say it twice?" arguments that
listening alone will not.

---

## 2. Reaching the two triggers

The farm warning is easy to force; the update banner is not.

**Farm warning** — block the account read and reload:

1. Open DevTools → Network → request blocking, add `*/api/v1/account`.
2. Reload and sign in. The warning appears at the top of the content pane.
3. For the *stale* variant instead: sign in normally first, then add the block,
   then go to **Settings** and save. The read after the save fails, and the
   wording changes to the "a re-read failed" one.

**Update banner** — a second service worker has to install and park in
`waiting`. There is no in-app affordance and Playwright cannot provoke it (see
the header of `tools/simulation/ui/specs/pwa.spec.ts`). Do it by hand:

1. Load the app once so the current worker is active.
2. Rebuild the image with any trivial SPA change (a character in a string is
   enough) and restart the `app` container, so `/sw.js` is byte-different.
3. DevTools → Application → Service Workers → **Update**. The new worker
   installs and stops at `waiting`, and the banner appears.

To get a message to arrive **while a dialog is open** — scenarios 2, 3 and 6,
which are the whole point — open the dialog *first*, then perform the last step
of whichever trigger you are using (add the network block and let a retry fire,
or press Update).

---

## 3. Scenarios

For each: **PASS** = the expected utterance, once. Record the actual speech.

| # | Setup | Expect | A failure sounds like |
|---|---|---|---|
| 1 | Update banner appears, **no dialog open** | "A new version is ready" spoken once | Silence, or the sentence twice (the offscreen region duplicating the banner) |
| 2 | Update banner appears **while a dialog is open** | Silence while the dialog is up; spoken **once** just after it closes | Silence after closing too — this is #485 unfixed |
| 3 | As 2, but with **two dialogs** stacked | Spoken only after the **last** one closes | Spoken as the inner dialog closes, while still inside the outer one |
| 4 | Farm warning fails **while a dialog is open** | Same as 2, with the warning wording | As 2 |
| 5 | A **standing** banner, dialogs opened and closed repeatedly | Nothing re-spoken on any close | The warning read out on every close — the nagging #499's retain rule prevents |
| 6 | Message raised in the **same commit** that opens a dialog; and again in the one that closes the last dialog | Either once or twice — **record which** | (Not a failure either way; see below) |

**Scenario 6 has no expected answer, and that is deliberate.** #499 cannot tell
whether the browser processed the live-region mutation before or after the
`inert` flip in that same commit, so it errs toward announcing — a duplicate
beats silence. This scenario measures what actually happens. If it is a
duplicate on every pairing, the predicate can be tightened; if silence, it
cannot.

---

## 4. What the result decides

#501 records two designs, each of which **deletes** the inference #499 makes
about whether the visible banner already spoke, rather than sharpening it.
`a11y-live-regions.spec.ts` already records Chromium's half of each; this pass
supplies the AT half.

- **Exempt the offscreen region from the inert sweep.** It would never be
  inert, would announce immediately, and every same-commit race disappears.
  Chromium's part is confirmed by the spec (un-inerting one subtree while a
  dialog is open does return it to the tree). What is unknown is whether an AT
  announces from a region inside an otherwise-inert page.
- **Let the offscreen region be the only announcer**, with the visible banners
  keeping `role="alert"` for the E2E vocabulary but carrying `aria-live="off"`.
  Chromium's part is confirmed by the spec (`aria-live="off"` does suppress the
  implicit politeness of `role="alert"`, where a plain `role="alert"` control
  still reports `assertive`). What is unknown is whether every AT honours that
  suppression — one that does not would announce twice, forever.

A scenario 1 or 5 failure means the current design speaks twice and should be
replaced. A scenario 2/3/4 failure means #485 is not actually fixed.

---

## 5. Constraints — do not "fix" a finding by breaking these

- **The visible banners must keep `role="alert"` / `role="status"`.** About 20
  error banners across the app use `role="alert"`, and the E2E suite reads its
  absence as "nothing has gone wrong". A permanently-mounted element wearing
  one answers every such query: shipping one turned 10 Playwright assertions
  into tautologies (caught by CI during #499). The offscreen announcers
  therefore spell out `aria-live` + `aria-atomic` and claim **no role**.
- **`.sr-only` is a 1px clipped box, not `display:none`** — Playwright counts
  it **visible**. Do not reach for a visibility assertion to tell the two
  regions apart.
- **Announcers must be inserted empty and populated later** (ARIA22, above).
  Seeding one with its message at mount is the Safari/VoiceOver failure.

## 6. Reporting

Post the filled table to the issue that sent you here, with AT and browser
build numbers and the speech transcript for any row that failed. A row nobody
ran is reported as **not run** — never left blank, which reads as a pass.
