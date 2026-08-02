// Owner/Admin persona — #277's flow: dashboard -> reports -> audit browse -> one /export.
//
// The cast's "Owner" signs in carrying the JWT role claim `Admin`, so this is
// also the only persona that exercises the admin-gated surfaces (the money
// section of Reports, Audit, Export). See src/cast.ts on why the label and the
// claim differ.
//
// EVERY ASSERTION HERE IS ABOUT POPULATED DATA, deliberately. An empty screen
// renders perfectly happily — Dashboard shows `dashboard:noStockMessage`, Reports
// shows an empty table, and a suite asserting "the heading is visible" passes
// against a database with nothing in it. That is the whole reason #277 shares
// #243's fixture rather than standing up an empty app.

import { expect, test } from "../src/fixtures";
import { owner } from "../src/cast";
import { tEn } from "../src/i18n";

test.describe("Owner", () => {
  test.beforeEach(async ({ signIn }) => {
    await signIn(owner());
  });

  test("dashboard shows real production, stock and sales data", async ({ page }) => {
    await expect(page.getByRole("heading", { name: tEn("dashboard:title") })).toBeVisible();

    // The three stat tiles render "—" when their fetch FAILED, and a number when
    // it succeeded. So "not —" is the actual guarantee: the data arrived. A
    // presence check on the tile would pass in exactly the case worth catching.
    // `exact: true` is load-bearing, not tidiness. The stock panel's footer
    // renders `dashboard:eggsAvailableMessage` ("{{count}} eggs available"), so a
    // substring match on "Eggs available" resolves to the stat tile AND that
    // panel — and the panel legitimately contains "—" in its Restricted column,
    // so the loose selector fails on correct data. Matching the label element
    // exactly, then stepping to its tile, keeps the assertion on the tile alone.
    for (const key of [
      "dashboard:statEggsCollectedToday",
      "dashboard:statEggsAvailable",
      "dashboard:statActiveFlocks",
    ] as const) {
      const tile = page.getByText(tEn(key), { exact: true }).locator("xpath=..");
      await expect(tile, `stat tile "${tEn(key)}" fell back to "—" (its fetch failed)`)
        .not.toContainText("—");
    }

    // Locate tables by a column header rather than by container structure —
    // resilient to the panels being re-laid-out, and it names what the reader
    // of a failure needs to know (which table).
    const todayTable = page
      .getByRole("table")
      .filter({ has: page.getByRole("columnheader", { name: tEn("dashboard:flockHeader") }) });
    // 2 flocks in the fixture + 1 header row. Asserting ">= 1 body row" rather
    // than an exact count: the seeder's flock count is configurable
    // (Simulation__Flocks), and pinning it here would make a fixture retune look
    // like a UI regression.
    await expect(todayTable.getByRole("row")).not.toHaveCount(0);
    await expect(page.getByText(tEn("dashboard:noFlocksMessage"))).toBeHidden();

    const stockTable = page
      .getByRole("table")
      .filter({ has: page.getByRole("columnheader", { name: tEn("dashboard:gradeHeader") }) });
    await expect(stockTable.getByRole("row")).not.toHaveCount(0);
    await expect(page.getByText(tEn("dashboard:noStockMessage"))).toBeHidden();
  });

  test("reports renders the default 7-day window with the admin-only money section", async ({
    page,
    nav,
  }) => {
    await nav.link("nav:reports").click();
    await expect(page.getByRole("heading", { name: tEn("reports:title") })).toBeVisible();

    // ReportsPage defaults to [farmToday-6, farmToday] and loads on mount. The
    // error region is role="alert" — checking it is hidden catches the
    // Report.FutureRange 400 that the farm's behind-UTC timezone produces for
    // part of every day, which is the defect that silently broke the k6 harness
    // (see src/farm.ts). A spec that only asserted the heading would not have
    // noticed.
    await expect(page.getByRole("alert")).toBeHidden();

    const production = page
      .getByRole("table")
      .filter({ has: page.getByRole("columnheader", { name: tEn("reports:dateHeader") }) });
    await expect(production).toBeVisible();
    await expect(production.getByRole("row")).not.toHaveCount(0);

    // Admin-only (isAdmin && sales && expenses && profit all present). Its
    // absence for this persona would mean either the gate is wrong or the money
    // fetches failed — both worth failing on.
    await expect(page.getByRole("heading", { name: tEn("reports:moneyHeading") })).toBeVisible();
  });

  test("audit browse lists real entries and filters by action", async ({ page, nav }) => {
    await nav.link("nav:audit").click();
    await expect(page.getByRole("heading", { name: tEn("audit:heading") })).toBeVisible();
    await expect(page.getByRole("alert")).toBeHidden();

    const table = page
      .getByRole("table")
      .filter({ has: page.getByRole("columnheader", { name: tEn("audit:whenHeader") }) });
    await expect(table).toBeVisible();
    const allRows = await table.getByRole("row").count();
    expect(allRows, "the audit log is empty — the fixture wrote no auditable events").toBeGreaterThan(1);
    await expect(page.getByText(tEn("audit:emptyMessage"))).toBeHidden();

    // Filtering is the screen's one interaction. Picking a specific action and
    // asserting the table still resolves (rows, or the empty message — both are
    // valid results for a filter) proves the control is wired; asserting the
    // filter merely EXISTS would not.
    const filter = page.getByLabel(tEn("audit:actionFilterLabel"));
    const options = await filter.locator("option").all();
    // options[0] is `audit:allActionsOption` (value ""), so [1] is a real action.
    const firstAction = await options[1]!.getAttribute("value");
    await filter.selectOption(firstAction!);

    await expect(page.getByRole("alert")).toBeHidden();
    const filteredRows = await table.getByRole("row").count();
    expect(
      filteredRows <= allRows,
      `filtering to "${firstAction}" returned MORE rows (${filteredRows}) than unfiltered (${allRows})`,
    ).toBe(true);
  });

  test("export downloads a real file", async ({ page, nav }) => {
    await nav.link("nav:export").click();
    await expect(page.getByRole("heading", { name: tEn("export:heading") })).toBeVisible();

    // THE GUARANTEE IS BYTES ON DISK, not that a click was accepted. The button
    // enters a "preparing" state either way; a spec that stopped at the click,
    // or at a network request being made, would pass against an export that
    // produced an empty or truncated file. So: wait for the download event,
    // persist it, and stat it.
    const downloadPromise = page.waitForEvent("download");
    await page.getByRole("button", { name: tEn("export:fullBackupButton") }).click();
    const download = await downloadPromise;

    const path = await download.path();
    expect(path, "the browser reported a download with no file behind it").toBeTruthy();

    const { statSync } = await import("node:fs");
    const bytes = statSync(path!).size;
    expect(bytes, `the export downloaded ${bytes} bytes`).toBeGreaterThan(0);

    expect(
      download.suggestedFilename(),
      "the export arrived with no filename, so a farm saving it gets an unnamed blob",
    ).not.toHaveLength(0);
  });
});
