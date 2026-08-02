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
    // `tbody tr`, NOT getByRole("row") — the role selector counts the HEADER row
    // too, so `not.toHaveCount(0)` was satisfied by a table with nothing in it
    // (PR #390 review). Deleting every rendered body row while keeping the
    // headers left this green, which is the exact failure this spec exists to
    // catch. Asserting ">= 1 body row" rather than an exact count is still
    // deliberate: the seeder's flock count is configurable (Simulation__Flocks),
    // and pinning it would make a fixture retune look like a UI regression.
    await expect(todayTable.locator("tbody tr")).not.toHaveCount(0);
    await expect(page.getByText(tEn("dashboard:noFlocksMessage"))).toBeHidden();

    const stockTable = page
      .getByRole("table")
      .filter({ has: page.getByRole("columnheader", { name: tEn("dashboard:gradeHeader") }) });
    await expect(stockTable.locator("tbody tr")).not.toHaveCount(0);
    await expect(page.getByText(tEn("dashboard:noStockMessage"))).toBeHidden();

    // The test's name promises sales data, so it has to actually look at it.
    // Without this, deleting the Sales panel outright left the spec green — it
    // asserted production and stock and called that "and sales" (PR #390 review).
    const salesTable = page
      .getByRole("table")
      .filter({ has: page.getByRole("columnheader", { name: tEn("dashboard:refHeader") }) });
    await expect(salesTable.locator("tbody tr")).not.toHaveCount(0);
    await expect(page.getByText(tEn("dashboard:noOrdersMessage"))).toBeHidden();
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
    // tbody, not getByRole("row") — see the dashboard test above.
    await expect(production.locator("tbody tr")).not.toHaveCount(0);

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
    const allRows = await table.locator("tbody tr").count();
    expect(allRows, "the audit log is empty — the fixture wrote no auditable events").toBeGreaterThan(0);
    await expect(page.getByText(tEn("audit:emptyMessage"))).toBeHidden();

    // Filtering is the screen's one interaction, and the assertion has to be
    // about WHAT CAME BACK, not how much of it.
    //
    // An earlier version asserted `filteredRows <= allRows`. That cannot fail:
    // a filter the server ignores entirely returns every row, and
    // `allRows <= allRows` is true. Deleting the `action` handling server-side
    // would have left it green (PR #390 review).
    //
    // So: pick a real action and assert that EVERY remaining row is that action.
    // That is the guarantee the control claims, and a no-op filter fails it as
    // soon as the log holds more than one kind of event — which the assertion
    // below on `distinctActions` proves it does, rather than assuming it.
    const filter = page.getByLabel(tEn("audit:actionFilterLabel"));
    const actionCells = table.locator("tbody tr td:nth-child(3)");
    const before = (await actionCells.allInnerTexts()).map((a) => a.trim());

    // Count what is actually on screen and filter to one of THOSE actions.
    //
    // An earlier version took `options[1]` — the first entry in the filter's own
    // dropdown. That worked only because `manager.spec.ts` sorts before this file
    // and performs exactly one adjust per run, so the action happened to exist:
    // an unstated ordering coupling between two spec files (PR #390 review round
    // 2). Deriving the target from the rows in front of us has no such
    // dependency, and it cannot pick an action with zero rows.
    const counts = new Map<string, number>();
    for (const action of before) counts.set(action, (counts.get(action) ?? 0) + 1);
    expect(
      counts.size,
      "the audit log shows only one kind of action, so a no-op filter would be indistinguishable "
        + "from a working one — this spec cannot prove anything against this fixture",
    ).toBeGreaterThan(1);

    // The rarest visible action: the strictest subset available, so a no-op
    // filter is maximally obvious.
    const chosenLabel = [...counts.entries()].sort((a, b) => a[1] - b[1])[0]![0];
    const option = filter.locator("option").filter({ hasText: chosenLabel }).first();
    const chosenValue = await option.getAttribute("value");
    expect(chosenValue, `no filter option matches the rendered label "${chosenLabel}"`).toBeTruthy();
    await filter.selectOption(chosenValue!);

    // POLL, do not snapshot. `allInnerTexts()` does not auto-retry, and
    // AuditPage.load() clears `events` to null (unmounting the tbody entirely)
    // before the filtered page arrives — so reading once, immediately, can catch
    // the empty transient. The previous version's "wait" was
    // `expect(getByRole("alert")).toBeHidden()`, which is vacuous here: this
    // screen renders no alert on the success path, so the locator matches nothing
    // and resolves on its first poll. That reintroduced exactly the intermittency
    // the count-free rewrite was meant to remove, on a different axis
    // (PR #390 review round 2).
    //
    // A no-op filter never reaches "settled", so this still fails closed.
    await expect
      .poll(
        async () => {
          const rows = (await actionCells.allInnerTexts()).map((a) => a.trim());
          if (rows.length === 0) return "still-loading";
          return rows.every((a) => a === chosenLabel) ? "settled" : "mixed";
        },
        {
          message:
            `rows that are NOT "${chosenLabel}" survived the filter — it is not being applied`,
        },
      )
      .toBe("settled");

    await expect(page.getByRole("alert")).toBeHidden();
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
