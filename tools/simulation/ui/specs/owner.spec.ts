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
import { owner, readmeFarmOwner } from "../src/cast";
import { tEn } from "../src/i18n";

test.describe("Owner", () => {
  test.beforeEach(async ({ signIn }) => {
    await signIn(owner());
  });

  test("dashboard shows real production, stock and sales data", async ({ page }) => {
    await expect(page.getByRole("heading", { name: tEn("dashboard:title") })).toBeVisible();

    // #654 — every panel degrades to `dashboard:panelLoadError` on its own
    // failed read, and the page to `dashboard:loadFailed` when every read
    // failed. "No error text anywhere" is the guarantee that every read
    // arrived; a presence check on a heading would pass in exactly the case
    // worth catching.
    await expect(page.getByText(tEn("dashboard:loadFailed"))).toHaveCount(0);
    await expect(page.getByText(tEn("dashboard:panelLoadError"))).toHaveCount(0);

    // Capture status: at least one Today row (the seeder's flock count is
    // configurable, so ">= 1", never an exact count), and the empty state
    // hidden. #829 — the row is `role="group"`, not a `.capture-tile` class
    // locator; nothing else on the Dashboard renders that role, so this
    // stays a stable, English-independent hook the same way the class was.
    await expect(page.getByRole("group").first()).toBeVisible();
    await expect(page.getByText(tEn("dashboard:noFlocksMessage"))).toBeHidden();

    await expect(page.getByRole("table", { name: tEn("dashboard:stockLedgerLabel") }).getByRole("row").nth(1)).toBeVisible();
    await expect(page.getByText(tEn("dashboard:noStockMessage"))).toBeHidden();

    // Stock is a table; sales has a named list inside its panel.
    // This is the positive control for ReadOnly's absent-panel assertion.
    const salesPanel = page.locator("section").filter({
      has: page.getByRole("link", { name: tEn("dashboard:recentOrdersTitle"), exact: true }),
    });
    await expect(salesPanel).toBeVisible();
    const salesList = salesPanel.getByRole("list", { name: tEn("dashboard:salesPanelTitle") });
    await expect(salesList.getByRole("listitem").first()).toBeVisible();
    await expect(page.getByText(tEn("dashboard:noOrdersMessage"))).toBeHidden();
  });

  test("stock rows highlight on hover and keyboard focus", async ({ page }) => {
    await page.goto("/");
    const stock = page.getByRole("table", { name: tEn("dashboard:stockLedgerLabel") });
    const row = stock.getByRole("row").nth(1);
    await expect(row).toBeVisible();
    await row.hover();
    const highlight = () => row.evaluate((element) => ({
      outline: getComputedStyle(element).outlineStyle,
      width: getComputedStyle(element).outlineWidth,
      text: getComputedStyle(element.querySelector("th")!).textDecorationLine,
    }));
    expect(await highlight()).toEqual({ outline: "solid", width: "2px", text: "underline" });
    await page.mouse.move(0, 0);
    await page.getByRole("heading", { name: tEn("dashboard:stockPanelTitle"), exact: true }).getByRole("link").focus();
    await page.keyboard.press("Tab");
    await expect(row).toBeFocused();
    expect(await highlight()).toEqual({ outline: "solid", width: "2px", text: "underline" });
    await page.keyboard.press("Tab");
    await expect(stock.getByRole("row").nth(2)).toBeFocused();
  });

  test("the outlined Record action stays in the count column with its brand colour", async ({ page }) => {
    const recordButton = page.getByRole("link", { name: /^Record / }).first();
    await expect(recordButton).toBeVisible();

    const [buttonColor, brandColor] = await recordButton.evaluate((el) => {
      const probe = document.createElement("span");
      probe.style.color = "var(--brand)";
      document.body.appendChild(probe);
      const resolved = getComputedStyle(probe).color;
      probe.remove();
      return [getComputedStyle(el).color, resolved];
    });
    expect(buttonColor).toBe(brandColor);
    await expect(recordButton).toHaveText(tEn("dashboard:recordAction"));
    await expect(recordButton).toHaveClass(/MuiButton-outlined/);
    const box = await recordButton.boundingBox();
    expect(box?.height).toBeGreaterThanOrEqual(44);
    const placement = await recordButton.evaluate((el) => {
      const style = getComputedStyle(el);
      return [style.gridColumn, style.gridRow];
    });
    expect(placement).toEqual(["3", "1"]);
  });

  // The three-column collection row holds the icon, name/status and count.
  // Its draft status must still fit on one line at desktop width (#883).
  test("a Draft row's status cell never wraps at 1280 (#883 round 5)", async ({ page, signIn }) => {
    // The simulation farm caps Today at twelve rows and every one of its
    // 101 houses is unrecorded, so a draft created here never reaches the
    // list. The demo farm (readme-farm) seeds House 1 as a draft for today,
    // which is the row whose "Draft, saved HH:MM" text is the wrap risk.
    await page.context().clearCookies();
    await page.evaluate(() => { localStorage.clear(); sessionStorage.clear(); });
    await signIn(readmeFarmOwner());
    await page.goto("/");
    const status = page.getByRole("group").getByText(/^Draft, saved/).first();
    await expect(status, "the demo farm seeds one draft entry for today, and none rendered").toBeVisible();
    const [box, lineHeight] = await Promise.all([
      status.boundingBox(),
      status.evaluate((el) => parseFloat(getComputedStyle(el).lineHeight)),
    ]);
    if (box === null) throw new Error("the status cell has no bounding box, so it is not rendered");
    expect(
      box.height,
      `the status cell rendered ${box.height}px tall against a ${lineHeight}px line height — it wrapped `
        + "onto more than one line",
    ).toBeCloseTo(lineHeight, 0);
  });

  test("recent sales amounts share one right edge at 1280 (#883 round 5)", async ({ page }) => {
    const salesList = page.getByRole("list", { name: tEn("dashboard:salesPanelTitle") });
    await expect(salesList.getByRole("listitem").first()).toBeVisible();
    const amounts = salesList.locator(".num");
    const count = await amounts.count();
    expect(count, "the sales list rendered no numeral cells to measure alignment against")
      .toBeGreaterThan(1);

    const edges = await amounts.evaluateAll((els) => els.map((el) => el.getBoundingClientRect().right));
    const first = edges[0]!;
    for (const [i, edge] of edges.entries()) {
      expect(
        edge,
        `sales row ${i}'s amount right edge is ${edge}, row 0's is ${first} — the amounts do not align`,
      ).toBeCloseTo(first, 0);
    }
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
