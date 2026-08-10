// Live regions under a modal (#485 / #501), in a REAL browser.
//
// ================== WHY THIS SPEC EXISTS ==================
//
// #483 makes everything except the topmost dialog `inert`. #485 was the
// consequence nobody intended: the offscreen announcers and the banners they
// back are `inert` too, so a message that arrives while a dialog is open is
// never announced, and un-inerting later replays nothing.
//
// The unit tests in `web/` cannot see any of that. **jsdom implements neither
// live regions nor the `inert` IDL** — verified during #499, not assumed:
// `'inert' in element` is `false` and a click passes straight through an
// `inert` subtree. So every jsdom test of this feature exercises the app's own
// JS bookkeeping (`anyDialogOpen()`) and never the browser behaviour that
// bookkeeping exists to compensate for.
//
// This spec supplies the half only a real browser can: what Chromium's
// accessibility tree actually does when a dialog opens.
//
// ================== WHAT THIS SPEC DOES NOT COVER ==================
//
// Two gaps, both real, neither closable here. They are the reason #501 stays
// open after this lands.
//
// 1. **No screen reader is involved.** Presence in the accessibility tree is
//    the PRECONDITION for an utterance, not the utterance. Whether NVDA, JAWS
//    or VoiceOver actually speaks — and speaks once — is a manual pass:
//    `docs/runbooks/screen-reader-verification.md`.
//
// 2. **The missed-then-delivered path cannot be driven from here at all**, so
//    the central behaviour of #499's fix is untested by this file. There is no
//    product affordance that makes a message ARRIVE while a dialog is open:
//      * the update banner needs a second service worker to park in `waiting`,
//        which Playwright cannot provoke (probed in pwa.spec.ts — the update
//        fetch is invisible to the browser context);
//      * the farm warning needs `/account` to fail on a RE-READ, and the only
//        two callers of that re-read are the banner's own retry button and the
//        settings save — neither of which can run with a dialog open.
//    Adding a test-only trigger to the app was considered and rejected; the
//    manual pass covers it instead.
//
// What is left is still worth having, and is exactly what jsdom could never
// say: the announcer really does leave the accessibility tree under a dialog
// (#485's premise, previously only inferred from #483's source), it really
// does come back, and a standing warning is not re-announced every time a
// dialog closes.

import type { Page } from "@playwright/test";
import { expect, test } from "../src/fixtures";
import { owner } from "../src/cast";
import { tEn } from "../src/i18n";
import { attachAx } from "../src/ax";

// The two always-mounted offscreen announcers (#485). Selected by their live
// attributes rather than a role, because they deliberately have NO role — a
// permanently mounted `role="alert"`/`role="status"` would answer the
// app-wide "is anything wrong on screen" queries and quietly retire them
// (that regression shipped once in #499 and CI caught it).
const FARM_ANNOUNCER = 'main.content > p.sr-only[aria-live="assertive"]';
// `UpdatePrompt` renders outside the router and the auth gate, so its region is
// a direct child of `#root`. Anchored there rather than left as a bare
// `p.sr-only[aria-live="polite"]`: CDP's querySelector returns the FIRST match,
// and a screen that grows its own polite region would silently retarget every
// assertion below onto the wrong element.
const UPDATE_ANNOUNCER = '#root > p.sr-only[aria-live="polite"]';

// The roles that mean "a message is on screen" to this app's own E2E suite.
// A permanently mounted element holding one answers every such query and
// retires it — the regression that shipped once in #499 and was caught by CI.
const LIVE_ROLES = ["alert", "status", "log", "marquee", "timer"];

/**
 * Give the app a bounded moment to do the thing this spec hopes it will NOT do.
 *
 * Every "the announcer stayed empty" assertion is a claim about a non-event,
 * and a non-event cannot be waited for: reading immediately after the dialog
 * closes would also pass if the write simply had not happened YET. So the read
 * is delayed past the point where the close path — inert removal, the modal
 * notification microtask, React's re-render and effects — has certainly run.
 *
 * Stated honestly, because the mutation check does NOT validate this bound:
 * `a11y-announcer-duplicates-banner` writes from a MutationObserver, which
 * lands sooner than React would, so its red proves the assertion can fail but
 * not that 250ms is enough for a slower regression. It is a bound, not a proof.
 */
async function settleNonEvent(page: Page): Promise<void> {
  await page.waitForTimeout(250);
}

/**
 * Record EVERY non-empty write to a live region, for the whole test.
 *
 * A point-in-time read ("the announcer is empty now") is a weak way to assert
 * a non-event: a write landing a moment after the read is invisible, and on the
 * PR runner — shared, `workers: 1`, `retries: 0`, nobody re-reading a green —
 * that is exactly where a too-short wait turns into a false pass. A
 * MutationObserver installed before the first dialog cycle catches a write
 * whenever it lands, so the claim strengthens from "was empty when I looked" to
 * "was never written", and the bounded wait stops being load-bearing.
 */
async function recordAnnouncerWrites(page: Page, selector: string): Promise<void> {
  await page.evaluate((sel) => {
    const target = document.querySelector(sel);
    if (target === null) throw new Error(`recordAnnouncerWrites: no element matches ${sel}`);
    const seen: string[] = [];
    (window as unknown as { __announcerWrites: string[] }).__announcerWrites = seen;
    const note = () => {
      const text = target.textContent ?? "";
      if (text !== "" && seen[seen.length - 1] !== text) seen.push(text);
    };
    note();
    new MutationObserver(note).observe(target, {
      childList: true,
      characterData: true,
      subtree: true,
    });
  }, selector);
}

async function announcerWrites(page: Page): Promise<string[]> {
  return page.evaluate(
    () => (window as unknown as { __announcerWrites?: string[] }).__announcerWrites ?? [],
  );
}

/**
 * Wait until the modal effect has run — WITHOUT asserting its outcome.
 *
 * **`toBeVisible()` on the dialog is not a settling signal for this.** The
 * dialog's own DOM presence is committed during render, but `syncModalBackground()`
 * runs from `pushModal`/`popModal` inside a plain `useEffect` — a passive effect
 * that fires after paint. So the two land in different frames, and a read taken
 * straight after `toBeVisible()` races the state it depends on.
 *
 * The signal is `document.body.style.overflow`, and the choice is deliberate.
 * `pushModal` sets it on the line *before* it calls `syncModalBackground()`, and
 * `popModal` restores it on the line before its own call, so observing it proves
 * the sweep has run — while saying nothing about what the sweep DID.
 *
 * **That holds for ONE dialog, which is all this spec opens, and breaks for a
 * nested one — so do not reuse this helper for a stack without fixing it.**
 * `pushModal` sets `overflow` unconditionally, so it is already `"hidden"` when
 * a second dialog opens and this would return on the first poll, having proved
 * nothing about the second sweep. `popModal` restores it only when the stack
 * empties, so closing an inner dialog would leave this polling for a change
 * that never comes. Scoped rather than stated absolutely, because an unqualified
 * version of this claim is the exact defect this PR has now corrected four times.
 *
 * The obvious alternative, polling `#root`'s `inert` attribute, was tried first
 * and is wrong: it is the sweep's own outcome, so `a11y-inert-sweep-removed`
 * killed the test HERE and execution never reached the accessibility-tree
 * assertions below. The mutation run went green while the assertions this spec
 * exists for had no mutant at all (codex round 1 on #504). A settling signal
 * must never be the thing under test.
 */
async function waitForModalEffect(page: Page, open: boolean): Promise<void> {
  await expect
    .poll(() => page.evaluate(() => document.body.style.overflow), {
      message: open
        ? "the dialog never locked body scroll — its open effect did not run, so nothing below "
          + "can be attributed to the modal sweep"
        : "body scroll was never restored — the dialog's close effect did not run",
    })
    .toBe(open ? "hidden" : "");
}

test.describe("live regions under a modal", () => {
  test("the offscreen announcers leave the accessibility tree while a dialog is open", async ({
    page,
    signIn,
  }) => {
    await signIn(owner());
    await page.goto("/customers");
    // `ax.node` is a single CDP round trip with NO auto-retry, unlike a
    // Playwright locator — so every read has to follow a settled signal. Wait
    // for the screen's own control before the first one; without this the
    // announcer reads as "not mounted" simply because React has not painted
    // yet (observed, first run of this spec).
    const newCustomer = page.getByRole("button", { name: tEn("customers:newCustomerButton") });
    await expect(newCustomer).toBeVisible();
    const ax = await attachAx(page);

    // Both announcers are mounted at all times and empty until they have
    // something to carry. Assert the ARRANGEMENT first: an absence assertion
    // below means nothing if the element was never there.
    for (const selector of [FARM_ANNOUNCER, UPDATE_ANNOUNCER]) {
      const before = await ax.node(selector);
      expect(before.inDom, `${selector} is not mounted`).toBe(true);
      expect(before.inTree, `${selector} is already out of the accessibility tree`).toBe(true);
      // Present is not the same as exposed. Chromium drops both `inert` and
      // `aria-hidden` subtrees from the tree outright (measured on 151), so
      // `inTree` already covers those — but a future markup or browser change
      // could keep the node and mark it ignored instead, which would be silent
      // in every other assertion here.
      expect(
        before.ignored,
        `${selector} is in the accessibility tree but IGNORED (${before.ignoredReasons.join(", ")}) `
          + `— present, and still invisible to a screen reader`,
      ).toBe(false);
      expect(before.text, `${selector} should start empty`).toBe("");
    }

    // ...and they are live regions Chromium agrees are live. `role` is
    // deliberately absent; the politeness has to come from the attributes.
    const farmBefore = await ax.node(FARM_ANNOUNCER);
    // The guarantee is that it claims NO live role, so that is what is
    // asserted — not `role === "paragraph"`, which would additionally pin a
    // Chromium role name this spec does not care about and would break on a
    // rename that harmed nothing.
    expect(
      LIVE_ROLES,
      "the farm announcer claimed a live ROLE — a permanently mounted one answers the app-wide "
        + "getByRole(\"alert\"/\"status\") queries and retires them",
    ).not.toContain(farmBefore.role);
    expect(farmBefore.live).toBe("assertive");
    expect(farmBefore.atomic).toBe(true);

    await newCustomer.click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toBeVisible();
    await waitForModalEffect(page, true);

    // THE POINT OF THE SPEC. Both announcers are still in the DOM and still
    // carry their live attributes, and Chromium has dropped them out of the
    // accessibility tree entirely — so anything written into them now is
    // written somewhere no assistive technology is looking.
    //
    // Read through `nodes()`, which resolves all three against ONE tree
    // snapshot. The third is a CONTROL, and not an optional one: "absent from
    // the tree" is also what a broken CDP session or a crashed page look like,
    // and a control taken from a LATER snapshot could not rule that out.
    const [farmDuring, updateDuring, inDialog] = await ax.nodes([
      FARM_ANNOUNCER,
      UPDATE_ANNOUNCER,
      '[role="dialog"] button',
    ]);
    for (const [selector, during] of [
      [FARM_ANNOUNCER, farmDuring!],
      [UPDATE_ANNOUNCER, updateDuring!],
    ] as const) {
      expect(during.inDom, `${selector} unmounted instead of going inert`).toBe(true);
      expect(
        during.inTree,
        `${selector} is still in the accessibility tree with a dialog open — the #483 inert `
          + `sweep is not reaching it, and #485's premise no longer holds`,
      ).toBe(false);
    }
    expect(
      inDialog!.inTree,
      "the dialog's own controls are missing from the accessibility tree too — the tree read is "
        + "broken, so the absences above prove nothing",
    ).toBe(true);

    await page.keyboard.press("Escape");
    await expect(dialog).toBeHidden();
    await waitForModalEffect(page, false);

    // ...and the page belongs to the announcers again. This is the un-inerting
    // that replays nothing, which is the whole reason #499's hook exists.
    for (const selector of [FARM_ANNOUNCER, UPDATE_ANNOUNCER]) {
      const after = await ax.node(selector);
      expect(after.inTree, `${selector} never returned to the accessibility tree`).toBe(true);
    }
  });

  test("a standing farm warning is announced once by the banner, and not re-announced as dialogs come and go", async ({
    page,
    signIn,
  }) => {
    // Fail the farm read for the whole session, so the warning is on screen
    // from the first paint. This is the "never loaded" variant: the shell has
    // no timezone, so every date field would otherwise follow the DEVICE's day
    // while looking perfectly healthy.
    await page.route("**/api/v1/account", (route) => route.abort());

    await signIn(owner());
    await page.goto("/customers");
    const ax = await attachAx(page);

    // THE ORDINARY PATH: the VISIBLE banner carries the text and keeps its
    // `role="alert"`, which is both how it announces itself and how it stays
    // in the app-wide alert vocabulary the rest of this suite reads.
    const banner = page.locator("p.farm-warning");
    await expect(banner).toContainText(tEn("nav:farmLoadFailedNeverLoaded"));
    const bannerAx = await ax.node("p.farm-warning");
    expect(bannerAx.inTree).toBe(true);
    expect(bannerAx.role, "the visible banner dropped out of the alert vocabulary").toBe("alert");

    // ...and the offscreen announcer stays EMPTY, because nothing was missed.
    // Both regions saying the same sentence is a screen reader saying it twice.
    await settleNonEvent(page);
    expect(
      (await ax.node(FARM_ANNOUNCER)).text,
      "the offscreen announcer duplicated a warning the visible banner already made",
    ).toBe("");

    // From here on, every write into the announcer is recorded, whenever it
    // lands — so a late one cannot slip between two point-in-time reads.
    await recordAnnouncerWrites(page, FARM_ANNOUNCER);

    // Open and close a dialog twice. The warning is old news by now; #499's
    // anti-nag rule is that closing a dialog must not make it speak again.
    for (let i = 0; i < 2; i++) {
      await page.getByRole("button", { name: tEn("customers:newCustomerButton") }).click();
      await expect(page.getByRole("dialog")).toBeVisible();
      await waitForModalEffect(page, true);
      await page.keyboard.press("Escape");
      await expect(page.getByRole("dialog")).toBeHidden();
      await waitForModalEffect(page, false);
      await settleNonEvent(page);

      expect(
        (await ax.node(FARM_ANNOUNCER)).text,
        `the offscreen announcer re-announced a standing warning after dialog cycle ${i + 1}`,
      ).toBe("");
    }

    // The cumulative check: across both cycles and everything after them, the
    // announcer was never written to at all.
    //
    // The observer removes the timing dependence BETWEEN reads, not at the end
    // of the test — inspecting the array is itself a moment, and a write
    // scheduled after it still escapes (codex round 1 on #504, correcting an
    // earlier claim here that the bound was no longer load-bearing). So the
    // final drain is long, and `a11y-announcer-writes-late` validates it by
    // writing well after the close: the bound is chosen against a mutant rather
    // than guessed. It bounds a REGRESSION's latency, which is unknowable in
    // general; for the real close path — microtasks and one React render, no
    // timers — it is enormous.
    await page.waitForTimeout(1200);
    expect(
      await announcerWrites(page),
      "the offscreen announcer was written to during the dialog cycles — a standing warning was "
        + "re-announced, which is the nagging #499's retain rule exists to prevent",
    ).toEqual([]);

    // The warning itself never went anywhere — otherwise the loop above would
    // pass simply because there was nothing left to announce.
    await expect(banner).toContainText(tEn("nav:farmLoadFailedNeverLoaded"));
  });

  // ================== RECORDED BROWSER FACTS ==================
  //
  // Not product behaviour. #501 lists two alternative designs, each of which
  // would DELETE the inference #499 currently makes about whether the visible
  // banner already spoke, and each of which rests on an assumption about the
  // browser that nobody had checked. This test checks them against elements it
  // injects itself, so what remains for a screen reader to confirm is only
  // whether the utterance follows.
  //
  // A failure here does NOT mean the app regressed — it means Chromium changed
  // its mind about something a future design was going to rely on, which is
  // worth being told about.
  test("recorded browser facts for the two deferred designs (#501) — not product behaviour", async ({
    page,
    signIn,
  }) => {
    await signIn(owner());
    await page.goto("/customers");
    const ax = await attachAx(page);

    // Record WHICH build produced these facts. The suite runs on two different
    // Chromium binaries — a system one on the NixOS dev box, a downloaded one
    // on CI — and the CDP Accessibility domain is experimental, so a future
    // failure should read "recorded on 151, failing on 158" rather than the
    // unanswerable "Chromium changed its mind".
    const build = page.context().browser()?.version() ?? "unknown";
    test.info().annotations.push({ type: "chromium", description: build });

    await page.evaluate(() => {
      const probe = document.createElement("div");
      probe.id = "ax-probe";
      const add = (id: string, attrs: Record<string, string>, text: string) => {
        const p = document.createElement("p");
        p.id = id;
        for (const [k, v] of Object.entries(attrs)) p.setAttribute(k, v);
        p.textContent = text;
        probe.append(p);
      };
      add("probe-alert-plain", { role: "alert" }, "loud");
      add("probe-alert-off", { role: "alert", "aria-live": "off" }, "quiet");
      add("probe-exempt", { "aria-live": "polite", "aria-atomic": "true" }, "exempt");
      document.body.append(probe);
    });

    // FACT 1 — does `aria-live="off"` actually silence an element that also
    // carries `role="alert"`? The "let the offscreen region be the only
    // announcer" design keeps the role for the E2E vocabulary and relies on
    // this pair resolving to "off".
    //
    // The CONTROL is load-bearing and the first run of this spec proved why.
    // Chromium does not report a `live` property of `"off"` — it reports NO
    // live property at all, which is also exactly what a plain non-live
    // paragraph reports. Asserting `live === null` on its own would therefore
    // have passed even if `aria-live="off"` were being ignored completely. The
    // pair is the evidence: same role, one attribute apart, different answers.
    const alertPlain = await ax.node("#probe-alert-plain");
    expect(alertPlain.role, "an explicit role=alert stopped resolving to alert").toBe("alert");
    expect(
      alertPlain.live,
      "role=alert no longer carries implicit assertive politeness — the control this fact is "
        + "measured against has moved",
    ).toBe("assertive");

    const alertOff = await ax.node("#probe-alert-off");
    expect(alertOff.role).toBe("alert");
    expect(
      alertOff.live,
      "aria-live=\"off\" no longer suppresses the implicit politeness of role=alert (the control "
        + "above still reports assertive) — the \"offscreen region is the only announcer\" design "
        + "in #501 rests on it doing so",
    ).toBeNull();

    // FACT 2 — would exempting one subtree from the inert sweep put it back in
    // the accessibility tree while a dialog is open? The `data-modal-exempt`
    // design rests on yes. #485 rejected that attribute for FOCUSABLE content
    // (a reachable control outside the modal defeats containment); a region
    // with no controls is a different case, which is why it is still open.
    await page.getByRole("button", { name: tEn("customers:newCustomerButton") }).click();
    await expect(page.getByRole("dialog")).toBeVisible();

    // Precondition, not the fact being recorded: the probe is a body child, so
    // the sweep marks it like any other. If this fails the app's sweep changed
    // scope, and the fact below would be measuring nothing.
    expect(
      (await ax.node("#probe-exempt")).inTree,
      "the injected probe is a body child but the modal sweep did not inert it — the sweep's "
        + "scope changed, so the exemption fact below is not being measured",
    ).toBe(false);

    await page.evaluate(() => document.getElementById("ax-probe")?.removeAttribute("inert"));

    expect(
      (await ax.node("#probe-exempt")).inTree,
      "un-inerting one subtree while a dialog is open did NOT return it to the accessibility "
        + "tree — the inert-exemption design in #501 would not work",
    ).toBe(true);
  });
});
