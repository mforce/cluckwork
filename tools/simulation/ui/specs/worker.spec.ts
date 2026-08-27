// Worker persona — daily entry by grade, and the flock-assignment boundary.
//
// ================== READ #388 BEFORE CHANGING THE SECOND TEST ==================
//
// #277 specifies this persona as "the flock-restricted worker sees only assigned
// flocks (403/hidden on the rest)". That is NOT what the application does — the
// write side uses a 422 refusal, not 403, and the read side uses symmetric 404
// filtering rather than a permission error. #277's core claim (the WRITE is
// gated) was established by probing the live fixture rather than by reading the
// issue:
//
//   POST /daily-entries, assigned flock    -> 201
//   POST /daily-entries, UNASSIGNED flock  -> 422 FlockScope.NotAssigned
//   GET  /flocks           (as the restricted worker) -> after #388: only the assigned flock
//   GET  /flocks/{id}      (unassigned)               -> after #388: 404
//   GET  /daily-entries?flockId= (unassigned)         -> after #388: no rows
//
// #388 LANDED read scoping (this PR): a restricted worker's READS are now
// filtered too — the flock list shows only the assigned flock (plus farm-wide
// rows), an unassigned flock's detail is 404, and the daily-entry / water
// / movement reads return only the assigned flock's rows. The write guard
// (422 FlockScope.NotAssigned) is unchanged, and this spec still asserts it,
// because the server-side guard remains the enforcement that matters — the
// read filter is not the write boundary. The second test therefore signs in
// as the UNRESTRICTED worker first to capture the unassigned flock's real id
// (the restricted picker no longer offers it), then injects a temporary
// option so the 422 write-guard path stays reachable — it does NOT hardcode a
// credential or a flock id, and it never uses out-of-band session injection
// (fixtures.ts refuses that). The read boundary itself is asserted in the
// dedicated read-scoping test below.

// RE-RUNNABILITY, since this spec writes and does NOT mint its own flock the way
// manager.spec.ts does. `RecordDailyEntry` is an UPSERT on
// (account, farm, house, flock, date) — a second POST for the same natural key
// updates the existing Draft rather than conflicting — so re-running against a
// dirty fixture is safe by the handler's own semantics, not by luck. It stops
// being safe only if a previous run left that day SUBMITTED, which nothing here
// does: this spec saves a draft and never submits. (Raised in the #390 review as
// a suspected re-run hazard; checked against the handler and by repeated
// consecutive runs, and it holds — but it was undocumented, which is the real
// defect being fixed here.)

import { expect, test } from "../src/fixtures";
import type { Page } from "@playwright/test";
import { restrictedWorker, unrestrictedWorker } from "../src/cast";
import { farmToday } from "../src/farm";
import { tEn } from "../src/i18n";
import { selectOptionContaining } from "../src/dom";

/** The flock SimulationDataSeeder assigns to worker #1 (`flockIds[0]`). */
const ASSIGNED_FLOCK = "Sim House A";
/** The flock it deliberately leaves unassigned, so the narrowing is genuine. */
const UNASSIGNED_FLOCK = "Sim House B";
const GRADES = ["Small", "Medium", "Large", "Jumbo", "Seconds"];

async function clearSeededGrades(page: Page) {
  for (const grade of GRADES)
    await page.getByLabel(grade, { exact: true }).fill("0");
}

test.describe("Worker", () => {
  test("records a daily entry by grade on an assigned flock", async ({ page, signIn, farm }) => {
    await signIn(restrictedWorker());
    await page.goto("/daily-entry");

    // The date field is bounded by `max={today}` from useFarmToday(), which
    // resolves through the FARM's zone. Typing a UTC-derived date here would be
    // rejected by the browser for part of every day (the farm is behind UTC) and
    // fail as "could not fill the field", pointing nowhere near the cause.
    const today = farmToday(farm.timeZoneId);
    await page.getByLabel(tEn("dailyEntry:dateLabel")).fill(today);
    await selectOptionContaining(page.getByLabel(tEn("dailyEntry:flockLabel")), ASSIGNED_FLOCK);

    // NumberField renders a real <input type="number"> with the label wired via
    // htmlFor, so getByLabel reaches the input and not the −/+ buttons.
    await page.getByLabel(tEn("dailyEntry:totalEggsLabel"), { exact: true }).fill("120");
    await page.getByLabel(tEn("dailyEntry:crackedLabel"), { exact: true }).fill("2");
    await page.getByLabel(tEn("dailyEntry:mortalityLabel"), { exact: true }).fill("1");
    await clearSeededGrades(page);
    await page.getByLabel("Small", { exact: true }).fill("114");

    await page.getByRole("button", { name: tEn("dailyEntry:saveDraftButton") }).click();

    // THE GUARANTEE IS THE SAVE LANDING, not the click being accepted. A success
    // message plus the absence of a refusal is what the worker actually sees.
    await expect(page.locator("p.error")).toBeHidden();
    await expect(page.locator("p.success")).toBeVisible();
  });

  test("is refused a daily entry on a flock it is not assigned to (#388)", async ({
    page,
    signIn,
    nav,
    farm,
  }) => {
    const today = farmToday(farm.timeZoneId);

    // #388 read scoping hides the unassigned flock from the restricted picker,
    // so this test can no longer select it directly. Capture its REAL id (the
    // flock id) as the unrestricted worker first, then sign out/in as the
    // restricted worker and inject ONE temporary <option> carrying that id —
    // this does NOT bypass the SPA's authenticated write path (the page still
    // sends the normal request with the module-held access token) and does NOT
    // hardcode a credential or a flock id (fixtures.ts refuses out-of-band
    // session injection; we never use page.request.post).
    await signIn(unrestrictedWorker());
    await page.goto("/daily-entry");
    const unassignedFlockId = await selectOptionContaining(
      page.getByLabel(tEn("dailyEntry:flockLabel")), UNASSIGNED_FLOCK);
    expect(unassignedFlockId).not.toBe("");

    await nav.signOut.click();
    await expect(page).toHaveURL(/\/login/);

    await signIn(restrictedWorker());
    await page.goto("/daily-entry");
    const select = page.getByLabel(tEn("dailyEntry:flockLabel"));

    // #388 read boundary: the restricted picker offers the assigned flock and
    // hides the unassigned one entirely.
    await expect(select.locator(`option[value="${unassignedFlockId}"]`)).toHaveCount(0);

    // Inject ONE temporary option so the server-side write guard is reachable.
    await select.evaluate(
      (el, { value, label }) => {
        const opt = document.createElement("option");
        opt.value = value;
        opt.textContent = label;
        el.appendChild(opt);
      },
      { value: unassignedFlockId, label: UNASSIGNED_FLOCK },
    );

    await page.getByLabel(tEn("dailyEntry:dateLabel")).fill(today);

    // Prefill-settle synchronization, mirroring manager.spec.ts:65-85
    // (openDailyEntryAwaitingPrefill). The page resets every count when prefill
    // settles, and filling before it creates the known CI-only race documented
    // at manager.spec.ts:103-106.
    const prefill = page.waitForResponse((r) =>
      r.url().includes("/daily-entries") && r.url().includes(unassignedFlockId) && r.ok());
    await select.selectOption(unassignedFlockId);
    await prefill;
    await expect(page.getByRole("button", { name: tEn("dailyEntry:saveDraftButton") })).toBeEnabled();

    // The refusal text comes from the SERVER's ProblemDetails detail, which is
    // English-only (server errors are not part of #182's translated catalogs) —
    // so matching English here is correct rather than a hardcoded-copy smell.
    // Matched loosely on the distinctive phrase, not the whole sentence. We
    // fill everything only after prefill has settled.
    await page.getByLabel(tEn("dailyEntry:totalEggsLabel"), { exact: true }).fill("120");
    await clearSeededGrades(page);
    await page.getByRole("button", { name: tEn("dailyEntry:saveDraftButton") }).click();

    await expect(
      page.getByText(/not assigned to this flock/i),
      "the unassigned-flock write was NOT refused — FlockScope enforcement may be gone",
    ).toBeVisible();
    await expect(page.locator("p.success")).toBeHidden();
  });

  test("an unrestricted worker CAN write to that same flock (control for #388)", async ({
    page,
    signIn,
    farm,
  }) => {
    // Without this, the previous test proves only "a worker was refused" — which
    // a blanket loss of Worker write access would satisfy just as well. Same
    // flock, same date, same fields; the only variable is the assignment.
    await signIn(unrestrictedWorker());
    await page.goto("/daily-entry");

    const today = farmToday(farm.timeZoneId);
    await page.getByLabel(tEn("dailyEntry:dateLabel")).fill(today);
    await selectOptionContaining(page.getByLabel(tEn("dailyEntry:flockLabel")), UNASSIGNED_FLOCK);
    await page.getByLabel(tEn("dailyEntry:totalEggsLabel"), { exact: true }).fill("130");
    await clearSeededGrades(page);
    await page.getByRole("button", { name: tEn("dailyEntry:saveDraftButton") }).click();

    await expect(page.getByText(/not assigned to this flock/i)).toBeHidden();
    await expect(page.locator("p.success")).toBeVisible();
  });

  test("is not offered the admin setup destinations", async ({ signIn, nav }) => {
    await signIn(restrictedWorker());
    for (const key of ["nav:users", "nav:audit", "nav:export", "nav:expenses", "nav:farmSettings"]) {
      await expect(nav.link(key)).toBeHidden();
    }
    // Control: a Worker IS a producer, so the production destinations are there.
    await expect(nav.link("nav:dailyEntry")).toBeVisible();
    await expect(nav.link("nav:flocks")).toBeVisible();
  });

  test("is read-scoped to its assigned flock on the daily-entry picker (#388)", async ({
    page,
    signIn,
    farm,
  }) => {
    await signIn(restrictedWorker());
    await page.goto("/daily-entry");

    // The read boundary, asserted where the worker actually sees it: the
    // picker offers the assigned flock and hides the unassigned one entirely.
    await expect(
      page.getByLabel(tEn("dailyEntry:flockLabel")).locator(`option:has-text("${ASSIGNED_FLOCK}")`),
    ).toHaveCount(1);
    await expect(
      page.getByLabel(tEn("dailyEntry:flockLabel")).locator(`option:has-text("${UNASSIGNED_FLOCK}")`),
    ).toHaveCount(0);

    // The SPA has no flock-detail route (only the /flocks list — see App.tsx),
    // so the unassigned-detail 404 contract is pinned by the API integration
    // test (FlockScopeTests.ScopedWorker_UnassignedFlockDetail_Returns404)
    // rather than here; the picker is this spec's read boundary.
  });
});
