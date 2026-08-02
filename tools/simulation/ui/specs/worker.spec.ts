// Worker persona — daily entry by grade, and the flock-assignment boundary.
//
// ================== READ #388 BEFORE CHANGING THE SECOND TEST ==================
//
// #277 specifies this persona as "the flock-restricted worker sees only assigned
// flocks (403/hidden on the rest)". That is NOT what the application does, which
// was established by probing the live fixture rather than by reading the issue:
//
//   POST /daily-entries, assigned flock    -> 201
//   POST /daily-entries, UNASSIGNED flock  -> 422 FlockScope.NotAssigned
//   GET  /flocks           (as the restricted worker) -> 200, BOTH flocks
//   GET  /flocks/{id}      (unassigned)               -> 200, full detail
//   GET  /daily-entries?flockId= (unassigned)         -> 200, full history
//
// So the assignment gates the production WRITE and nothing else. Whether the
// reads should also be scoped is a real open question, filed as **#388** — and
// per the standing rule, the failure links to the owning issue instead of the
// assertion being weakened to fit. This spec therefore asserts the guarantee the
// app genuinely provides (the write refusal), with an unrestricted worker as the
// control so "refused" is attributable to the assignment and not to the persona
// being unable to write at all.
//
// If #388 lands read scoping, add the read assertions here — do not replace the
// write ones, which will still be the enforcement that matters.

import { expect, test } from "../src/fixtures";
import { restrictedWorker, unrestrictedWorker } from "../src/cast";
import { farmToday } from "../src/farm";
import { tEn } from "../src/i18n";
import { selectOptionContaining } from "../src/dom";

/** The flock SimulationDataSeeder assigns to worker #1 (`flockIds[0]`). */
const ASSIGNED_FLOCK = "Sim House A";
/** The flock it deliberately leaves unassigned, so the narrowing is genuine. */
const UNASSIGNED_FLOCK = "Sim House B";

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

    await page.getByRole("button", { name: tEn("dailyEntry:saveDraftButton") }).click();

    // THE GUARANTEE IS THE SAVE LANDING, not the click being accepted. A success
    // message plus the absence of a refusal is what the worker actually sees.
    await expect(page.locator("p.error")).toBeHidden();
    await expect(page.locator("p.success")).toBeVisible();
  });

  test("is refused a daily entry on a flock it is not assigned to (#388)", async ({
    page,
    signIn,
    farm,
  }) => {
    await signIn(restrictedWorker());
    await page.goto("/daily-entry");

    const today = farmToday(farm.timeZoneId);
    await page.getByLabel(tEn("dailyEntry:dateLabel")).fill(today);
    await selectOptionContaining(page.getByLabel(tEn("dailyEntry:flockLabel")), UNASSIGNED_FLOCK);

    // #388: the unassigned flock IS offered in this dropdown, because the flock
    // list is not scoped by assignment. That is the current behaviour, and the
    // spec depends on it to reach the write at all — if #388 lands read scoping,
    // this selectOption is what will start failing, which is the right place to
    // notice.
    await page.getByLabel(tEn("dailyEntry:totalEggsLabel"), { exact: true }).fill("120");
    await page.getByRole("button", { name: tEn("dailyEntry:saveDraftButton") }).click();

    // The refusal text comes from the SERVER's ProblemDetails detail, which is
    // English-only (server errors are not part of #182's translated catalogs) —
    // so matching English here is correct rather than a hardcoded-copy smell.
    // Matched loosely on the distinctive phrase, not the whole sentence.
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
});
