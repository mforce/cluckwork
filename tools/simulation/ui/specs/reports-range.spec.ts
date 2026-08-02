// #311 acceptance — the report at its maximum supported date range.
//
// #311 is CLOSED; this is the regression layer, not the fix.
//
// ================== WHERE THE BOUND ACTUALLY LIVES ==================
//
// `src/Cluckwork.Api/Endpoints/Reports/ReportEndpoints.cs`:
//
//     private const int MaxRangeDays = 366;
//     if (t.DayNumber - f.DayNumber >= MaxRangeDays)
//         return Results.Problem($"Range cannot exceed {MaxRangeDays} days.",
//             statusCode: 400, title: "Report.RangeTooLarge");
//
// Note the `>=`. The largest range the server ACCEPTS is therefore a 365-day
// difference between `from` and `to` (a 366-day inclusive window) — not 366.
// Off-by-one here is the difference between testing the boundary and testing a
// point comfortably inside it, so the spec probes BOTH SIDES: the largest
// accepted range must work, and one day more must be refused. A test that only
// asserted the accepted side would still pass if the bound were quietly widened
// to a year and a half.
//
// ReportsPage enforces NO client-side cap — `from` is bounded only by `to`, and
// `to` only by farm-local today — so the browser genuinely sends these.

import { expect, test } from "../src/fixtures";
import { owner } from "../src/cast";
import { daysBefore, farmToday } from "../src/farm";
import { tEn } from "../src/i18n";

/** Mirrors ReportEndpoints.MaxRangeDays. The comparison there is `>=`. */
const MAX_RANGE_DAYS = 366;
/** The largest `to - from` the server accepts. */
const LARGEST_ACCEPTED_SPAN = MAX_RANGE_DAYS - 1;

const REPORT_PATH = /\/api\/v1\/reports\//;

test.describe("#311 report range", () => {
  test("renders at the maximum supported range, and records what it cost", async ({
    page,
    signIn,
    farm,
  }, testInfo) => {
    await signIn(owner());

    const to = farmToday(farm.timeZoneId);
    const from = daysBefore(to, LARGEST_ACCEPTED_SPAN);

    // Collect every report response the screen makes — ReportsPage fans out to
    // production, sales, expenses and profit, so a single waitForResponse would
    // time one of four and call it "the" latency.
    const timings: Array<{ path: string; status: number; ms: number }> = [];
    page.on("response", (res) => {
      if (!REPORT_PATH.test(res.url())) return;
      const url = new URL(res.url());
      if (url.searchParams.get("from") !== from || url.searchParams.get("to") !== to) return;
      const t = res.request().timing();
      timings.push({
        path: url.pathname,
        status: res.status(),
        ms: Math.round(t.responseEnd),
      });
    });

    await page.goto("/reports");
    await expect(page.getByRole("heading", { name: tEn("reports:title") })).toBeVisible();

    // Widen to the maximum. `to` first: `from`'s own `max` attribute is `to`, so
    // setting `from` to a far-past date while `to` is still the default would be
    // clamped by the browser and the spec would quietly test a 7-day window.
    //
    // `exact: true` on both: the labels are the single words "From" and "To",
    // and getByLabel substring-matches accessible names — so a bare "To" also
    // resolves the theme toggle ("Switch to night mode"). Two elements, strict
    // mode violation, and the failure names the toggle rather than the ambiguity.
    const startedAt = Date.now();
    await page.getByLabel(tEn("reports:toLabel"), { exact: true }).fill(to);
    await page.getByLabel(tEn("reports:fromLabel"), { exact: true }).fill(from);

    // THE GUARANTEE: the screen renders the report, rather than the bound
    // refusing it. `role="alert"` is ReportsPage's error region.
    const production = page
      .getByRole("table")
      .filter({ has: page.getByRole("columnheader", { name: tEn("reports:dateHeader") }) });
    await expect(production).toBeVisible();
    await expect(
      page.getByRole("alert"),
      `a ${LARGEST_ACCEPTED_SPAN}-day range was refused, but MaxRangeDays=${MAX_RANGE_DAYS} should accept it`,
    ).toBeHidden();
    const renderedInMs = Date.now() - startedAt;

    // The date fields really do hold the wide range — proof the browser did not
    // clamp what was typed, which would make every assertion above describe a
    // narrower request than the one this spec claims to have made.
    await expect(page.getByLabel(tEn("reports:fromLabel"), { exact: true })).toHaveValue(from);
    await expect(page.getByLabel(tEn("reports:toLabel"), { exact: true })).toHaveValue(to);

    const atMaxRange = timings.filter((t) => t.status === 200);
    expect(atMaxRange.length, "no report endpoint responded at the maximum range").toBeGreaterThan(0);

    // Recorded, not asserted. A latency THRESHOLD here would be a flaky test
    // dressed as a performance gate — the number depends on the host, and under
    // the canary (#386) it is supposed to degrade. #311's acceptance item asks
    // for the figure to be captured; the canary is where it gets compared.
    const slowest = Math.max(...atMaxRange.map((t) => t.ms));
    const summary = [
      `range: ${from} .. ${to} (${LARGEST_ACCEPTED_SPAN} days, the documented maximum)`,
      `server: ${atMaxRange.map((t) => `${t.path} ${t.ms}ms`).join(", ")}`,
      `slowest endpoint: ${slowest}ms`,
      `browser: table rendered ${renderedInMs}ms after the range was applied`,
    ].join("\n");
    await testInfo.attach("report-max-range-timing", {
      body: summary,
      contentType: "text/plain",
    });
    testInfo.annotations.push({ type: "#311 max-range timing", description: summary });
  });

  test("refuses one day beyond the documented bound", async ({ page, signIn, farm }) => {
    await signIn(owner());

    const to = farmToday(farm.timeZoneId);
    // One day wider than the largest accepted span — the first value the
    // server's `>=` rejects.
    const from = daysBefore(to, MAX_RANGE_DAYS);

    await page.goto("/reports");
    await page.getByLabel(tEn("reports:toLabel"), { exact: true }).fill(to);
    await page.getByLabel(tEn("reports:fromLabel"), { exact: true }).fill(from);

    // The user is TOLD. A silently empty table would be worse than the refusal:
    // it reads as "no data in that period" for a farm that has plenty.
    await expect(
      page.getByRole("alert"),
      `a ${MAX_RANGE_DAYS}-day range was accepted — the documented bound is not being enforced`,
    ).toBeVisible();

    // And the screen offers a way out rather than stranding them.
    await expect(page.getByRole("button", { name: tEn("common:retry") })).toBeVisible();
  });
});
