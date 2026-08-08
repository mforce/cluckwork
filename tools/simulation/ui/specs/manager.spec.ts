// Manager persona — #277's flow: daily-entry submit -> review -> adjust/void,
// plus flock ops.
//
// ================== WHY THIS CREATES ITS OWN FLOCK ==================
//
// The obvious version of this spec submits today's entry on a seeded flock. It
// works exactly once. Submitting moves the entry out of Draft, so the SECOND run
// finds it locked, the fields disabled, and fails — and it fails in a way that
// reads like a permissions regression rather than like the spec having eaten its
// own fixture.
//
// The repo's own note is that the fixture is throwaway and `reset.sh` is
// authorised, so "just reseed between runs" is available. It is still the wrong
// default: a five-minute wipe as the price of re-running one spec means the spec
// gets run rarely, and a suite that is expensive to re-run is a suite whose red
// gets argued with instead of investigated.
//
// So this creates a flock, which covers #277's "flock ops" in the same pass, and
// then owns a brand-new Draft entry on it that no other spec and no previous run
// can have touched. Re-runnable against a dirty fixture, by construction.
//
// The cost, stated plainly: each run leaves one more flock and one voided entry
// behind. That is fixture growth, not fixture corruption — nothing else asserts
// on flock COUNT (the Owner dashboard asserts "not zero rows", deliberately), and
// `reset.sh` clears it whenever anyone wants a clean slate.

import type { Page } from "@playwright/test";
import { expect, test } from "../src/fixtures";
import { castMember } from "../src/cast";
import { selectOptionContaining } from "../src/dom";
import { farmToday } from "../src/farm";
import { prefixOf, tEn } from "../src/i18n";

// #446 prefill: picking the flock fires a prefill that RESETS every count
// when it settles, so fills must not race it. `prefillPending` is only raised
// inside an effect — even a just-observed enabled save button can be the
// pre-effect render (codex review of 366de81). So synchronize on the network:
// the response wait is registered BEFORE selecting and keyed to this flock's
// id (the date-change prefill for the auto-defaulted flock must not satisfy
// it), and the enabled assertion afterwards proves the settle was APPLIED —
// the response existing proves the effect ran and disabled the buttons, so
// enabled-after-response can only be the post-reset state.
async function selectFlockAwaitingPrefill(page: Page, flockName: string) {
  const select = page.getByLabel(tEn("dailyEntry:flockLabel"));
  const option = select.locator("option", { hasText: flockName });
  await expect(option).toHaveCount(1);
  const flockId = (await option.getAttribute("value"))!;
  const prefill = page.waitForResponse((r) =>
    r.url().includes("/daily-entries") && r.url().includes(flockId) && r.ok());
  await select.selectOption(flockId);
  await prefill;
  await expect(page.getByRole("button", { name: tEn("dailyEntry:submitButton") })).toBeEnabled();
}

test.describe("Manager", () => {
  test("creates a flock, submits its entry, then adjusts and voids it", async ({
    page,
    signIn,
    farm,
  }) => {
    await signIn(castMember("Manager"));
    const today = farmToday(farm.timeZoneId);
    const flockName = `E2E Flock ${Date.now()}`;

    // ---- 1. Flock ops: create ----------------------------------------------
    await page.goto("/flocks");
    await page.getByRole("button", { name: tEn("flocks:newFlockButton") }).click();

    const newFlock = page.getByRole("dialog", { name: tEn("flocks:newFlockDialogTitle") });
    await newFlock.getByLabel(tEn("flocks:nameLabel")).fill(flockName);
    await newFlock.getByLabel(tEn("flocks:breedLabel")).fill("E2E Leghorn");
    await newFlock.getByLabel(tEn("flocks:placedLabel")).fill(today);
    await newFlock.getByLabel(tEn("flocks:birdsLabel"), { exact: true }).fill("50");
    await newFlock.getByRole("button", { name: tEn("flocks:addFlockButton") }).click();

    await expect(newFlock).toBeHidden();
    // The flock is on the farm's roster — the guarantee, not the dialog closing.
    await expect(page.getByRole("cell", { name: flockName })).toBeVisible();

    // ---- 2. Record and SUBMIT a daily entry on it --------------------------
    await page.goto("/daily-entry");
    await page.getByLabel(tEn("dailyEntry:dateLabel")).fill(today);
    // Prefill-settle synchronization — see selectFlockAwaitingPrefill. Without
    // it the prefill wipes the fills and the spec submits an all-zeros day
    // (PR #464's CI-only 46.9s red, caught frame-by-frame on video).
    await selectFlockAwaitingPrefill(page, flockName);
    await page.getByLabel(tEn("dailyEntry:totalEggsLabel"), { exact: true }).fill("40");
    await page.getByLabel(tEn("dailyEntry:crackedLabel"), { exact: true }).fill("1");
    // #394: submit is refused unless grading exactly reconciles sellable eggs
    // (40 total − 1 cracked = 39) — "Large" is a base default grade, present
    // on every install regardless of seed profile, so it's always offered here.
    await page.getByLabel("Large", { exact: true }).fill("39");

    await page.getByRole("button", { name: tEn("dailyEntry:submitButton") }).click();
    // Submitting is confirmed, not immediate. Stopping at the first click would
    // assert on a still-Draft entry and the rest of the spec would be testing
    // the wrong lifecycle state.
    await page
      .getByRole("dialog", { name: tEn("dailyEntry:confirmSubmitTitle") })
      .getByRole("button", { name: tEn("dailyEntry:confirmSubmitLabel") })
      .click();

    // THE STATE MOVED, and the way a user sees that is the screen going
    // read-only: DailyEntryPage sets `entryLocked` for any status past Draft and
    // shows the locked banner. Its text interpolates the status, so match the
    // stable prefix.
    await expect(
      page.getByText(prefixOf("en", "dailyEntry:entryLockedBanner")),
      "the entry did not leave Draft — submit did not take effect",
    ).toBeVisible();

    // ---- 3. Review it on History, then ADJUST ------------------------------
    await page.goto("/history");
    await selectOptionContaining(page.getByLabel(tEn("history:flockLabel")), flockName);

    const row = page.getByRole("row").filter({ hasText: flockName });
    await expect(row).toHaveCount(1);
    await expect(row).toContainText(tEn("history:statusSubmitted"));

    await row.getByRole("button", { name: tEn("history:adjustButton") }).click();

    // The adjust dialog's title interpolates the entry, so it is located by the
    // control only it has — the required reason field — rather than by a title
    // this spec would have to reconstruct.
    const adjustDialog = page
      .getByRole("dialog")
      .filter({ has: page.getByLabel(tEn("history:reasonLabel")) });
    // The dialog now mirrors the Daily entry form field for field, so its count
    // labels come from THAT screen's namespace (`dailyEntry`), not `history`.
    await adjustDialog.getByLabel(tEn("dailyEntry:totalEggsLabel"), { exact: true }).fill("38");
    // #394: an adjustment has no draft state — Save stays disabled unless
    // grading reconciles exactly. Cracked carries over at 1, so 38 − 1 = 37.
    await adjustDialog.getByLabel("Large", { exact: true }).fill("37");
    await adjustDialog.getByLabel(tEn("history:reasonLabel")).fill("E2E recount");
    await adjustDialog.getByRole("button", { name: tEn("history:saveAdjustmentButton") }).click();

    await expect(adjustDialog).toBeHidden();
    // HistoryPage announces success in a role="status" region — the same thing
    // the operator is told, and screen-reader users are told.
    //
    // Narrowed to the PARAGRAPH. A bare getByRole("status") is ambiguous here
    // and always will be: every BusyButton renders its own sr-only
    // `<span role="status">` sibling for the "Working…" announcement, so any row
    // carrying an action button contributes one. Matching `p[role="status"]`
    // keeps the accessibility meaning (this is the live announcement, not just
    // some green text) while excluding those.
    const announcement = page.locator('p[role="status"]');
    await expect(announcement).toContainText(tEn("history:entryAdjustedMessage"));
    // And the row now reads Adjusted. The message alone would pass against an
    // announcement fired on a write that did not land.
    await expect(row).toContainText(tEn("history:statusAdjusted"));

    // ---- 4. VOID it --------------------------------------------------------
    await row.getByRole("button", { name: tEn("history:voidButton") }).click();

    // Voiding goes through askReason(), whose title interpolates the date and
    // flock — located, again, by the confirm control rather than by the title.
    const voidDialog = page
      .getByRole("dialog")
      .filter({ has: page.getByRole("button", { name: tEn("history:voidConfirmLabel") }) });
    await voidDialog.getByRole("textbox").fill("E2E void");
    await voidDialog.getByRole("button", { name: tEn("history:voidConfirmLabel") }).click();

    await expect(announcement).toContainText(tEn("history:entryVoidedMessage"));
    await expect(row).toContainText(tEn("history:statusVoided"));
  });

  // #406 — the corrective tier's newest verb: a standalone per-lot write-off.
  // Same self-owned-fixture doctrine as the flow above: this test creates its
  // own flock and entry, so the lot it writes off is one no other spec and no
  // previous run can have touched. The egg count is unique-ish per run so the
  // lot row can be located unambiguously against a dirty fixture.
  test("writes off lost stock from its own lot without touching the entry", async ({
    page,
    signIn,
    farm,
  }) => {
    await signIn(castMember("Manager"));
    const today = farmToday(farm.timeZoneId);
    const flockName = `E2E WriteOff Flock ${Date.now()}`;
    // 41–98, effectively unique per run — and disjoint from the 38–40 the
    // flow spec above uses, so its same-day lot can never match the filter.
    const eggs = 41 + (Date.now() % 58);

    await page.goto("/flocks");
    await page.getByRole("button", { name: tEn("flocks:newFlockButton") }).click();
    const newFlock = page.getByRole("dialog", { name: tEn("flocks:newFlockDialogTitle") });
    await newFlock.getByLabel(tEn("flocks:nameLabel")).fill(flockName);
    await newFlock.getByLabel(tEn("flocks:breedLabel")).fill("E2E Leghorn");
    await newFlock.getByLabel(tEn("flocks:placedLabel")).fill(today);
    await newFlock.getByLabel(tEn("flocks:birdsLabel"), { exact: true }).fill("50");
    await newFlock.getByRole("button", { name: tEn("flocks:addFlockButton") }).click();
    await expect(newFlock).toBeHidden();

    await page.goto("/daily-entry");
    await page.getByLabel(tEn("dailyEntry:dateLabel")).fill(today);
    await selectFlockAwaitingPrefill(page, flockName);
    await page.getByLabel(tEn("dailyEntry:totalEggsLabel"), { exact: true }).fill(String(eggs));
    await page.getByLabel("Large", { exact: true }).fill(String(eggs));
    await page.getByRole("button", { name: tEn("dailyEntry:submitButton") }).click();
    await page
      .getByRole("dialog", { name: tEn("dailyEntry:confirmSubmitTitle") })
      .getByRole("button", { name: tEn("dailyEntry:confirmSubmitLabel") })
      .click();
    await expect(page.getByText(prefixOf("en", "dailyEntry:entryLockedBanner"))).toBeVisible();
    // The lot EXISTS, pinned by the created-count in the success message — a
    // wiped form submits an all-zeros day whose banner looks identical but
    // creates no lot, and the spec would then time out far away on /stock.
    await expect(page.getByText(
      tEn("dailyEntry:submittedMessage").replace("{{count}}", "1"))).toBeVisible();

    // ---- The write-off itself ---------------------------------------------
    await page.goto("/stock");
    await page
      .getByRole("row")
      .filter({ has: page.getByRole("cell", { name: "Large", exact: true }) })
      .getByRole("button", { name: tEn("stock:lotsButton") })
      .click();

    // Exact CELL matches, not hasText: a grade summary row's 5-digit balance
    // contains any 2-digit count as a substring (that exact false match
    // shipped in this spec's first draft). Today's date + the exact produced
    // count pins the one lot this test created.
    const lotRow = page
      .getByRole("row")
      .filter({ has: page.getByRole("cell", { name: today, exact: true }) })
      .filter({ has: page.getByRole("cell", { name: String(eggs), exact: true }) })
      .first();
    await lotRow.getByRole("button", { name: tEn("stock:writeOffButton") }).click();

    const writeOff = page
      .getByRole("dialog")
      .filter({ has: page.getByRole("button", { name: tEn("stock:writeOffSubmitButton") }) });
    await writeOff.getByLabel(tEn("stock:writeOffQuantityLabel"), { exact: true }).fill("2");
    await writeOff.getByLabel(tEn("stock:writeOffReasonLabel")).fill("E2E cooler breakage");
    await writeOff.getByRole("button", { name: tEn("stock:writeOffSubmitButton") }).click();

    await expect(writeOff).toBeHidden();
    const announcement = page.locator('p[role="status"]');
    await expect(announcement).toContainText(prefixOf("en", "stock:writeOffRecordedMessage"));
    // The lot row shows the new balance; produced is untouched — the write-off
    // never restated the day's laying.
    await expect(lotRow.getByRole("cell", { name: String(eggs - 2), exact: true })).toBeVisible();
    await expect(lotRow.getByRole("cell", { name: String(eggs), exact: true })).toBeVisible();
  });

  test("can reach the admin destinations a Worker cannot", async ({ signIn, nav }) => {
    // The mirror of the Worker spec's gate assertion. Manager is isAdmin, so the
    // Setup group is present — EXCEPT Users, which nav.tsx narrows to role
    // "Admin" alone. That one exclusion is the interesting part: it is the
    // difference between "isAdmin" and "Admin", and it is easy to regress into
    // showing a Manager the user-management screen.
    await signIn(castMember("Manager"));
    await expect(nav.link("nav:audit")).toBeVisible();
    await expect(nav.link("nav:export")).toBeVisible();
    await expect(nav.link("nav:expenses")).toBeVisible();
    await expect(nav.link("nav:farmSettings")).toBeVisible();
    await expect(
      nav.link("nav:users"),
      "the sidebar offered Users to a Manager — nav.tsx gates that on role === 'Admin'",
    ).toBeHidden();
  });
});
