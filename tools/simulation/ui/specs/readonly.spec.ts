// ReadOnly persona — read-heavy browsing, and the deep-link refusals.
//
// ================== WHY THE 403 IS THE ASSERTION ==================
//
// #277 is explicit: there is NO route-level gate in the SPA. `/audit` and
// `/users` are ordinary routes behind ProtectedRoute like every other, so a
// ReadOnly user who types the URL DOES reach the screen — and is refused by the
// server when it fetches. Hiding the nav link is a convenience, not a boundary.
//
// So this spec asserts both halves, because they are different guarantees and
// only one of them is security:
//
//   * the link is not offered            — nav.tsx's role gate (cosmetic)
//   * the deep link is refused anyway    — the server (the actual boundary)
//
// Asserting only the first would pass against a build where the server-side
// policy had been removed entirely, which is precisely the regression worth
// catching. #127 was that bug: ReadOnly could read customer PII and sales
// financials because the reads were ungated while the nav pretended otherwise.

import { expect, test } from "../src/fixtures";
import { castMember } from "../src/cast";
import { tEn } from "../src/i18n";

test.describe("ReadOnly", () => {
  test.beforeEach(async ({ signIn }) => {
    await signIn(castMember("ReadOnly"));
  });

  test("browses the screens it is allowed, with real data", async ({ page, nav }) => {
    // Dashboard. The Sales panel is role-gated OUT for ReadOnly (Dashboard.tsx
    // renders it only when role is neither ReadOnly nor Denied), so its absence
    // is part of what "correct for this persona" means.
    await expect(page.getByRole("heading", { name: tEn("dashboard:title") })).toBeVisible();
    await expect(page.getByRole("link", { name: tEn("dashboard:salesPanelTitle") })).toBeHidden();

    // Stock — allowed, and populated.
    await nav.link("nav:stock").click();
    await expect(page.getByRole("heading", { name: tEn("stock:title") })).toBeVisible();
    await expect(page.getByText(tEn("stock:noStockMessage"))).toBeHidden();
    await expect(page.getByRole("alert")).toBeHidden();

    // #406: the per-lot write-off is corrective-tier. Drill into the first
    // grade's lots and assert the action is not offered (cosmetic half —
    // RoleMatrixTests owns the server-side 403).
    await page.getByRole("button", { name: tEn("stock:lotsButton") }).first().click();
    await expect(page.getByRole("heading", { name: tEn("stock:lotsHeading") })).toBeVisible();
    await expect(page.getByRole("button", { name: tEn("stock:writeOffButton") })).toHaveCount(0);
    await expect(page.getByText(tEn("stock:writeOffNeedsAdminMessage"))).toBeVisible();

    // History — allowed for every role.
    await nav.link("nav:history").click();
    await expect(page.getByRole("heading", { name: tEn("history:title") })).toBeVisible();
    await expect(page.getByText(tEn("history:noEntriesMatch"))).toBeHidden();
    await expect(page.getByRole("alert")).toBeHidden();

    // Reports — allowed, but the money section is admin-only. Its ABSENCE here
    // is the assertion: ReadOnly seeing farm revenue would be #127 again.
    await nav.link("nav:reports").click();
    await expect(page.getByRole("heading", { name: tEn("reports:title") })).toBeVisible();
    await expect(page.getByRole("heading", { name: tEn("reports:moneyHeading") })).toBeHidden();
  });

  // #465 — the fixture's 90-day × 2-flock history gives every graded grade
  // ~176 lots, so the drill-down MUST page: exactly one 50-lot page first,
  // then load-more appends the next. Growth-after-click is the load-bearing
  // assertion — the stock-pager-inert mutant serves page one for every
  // offset, and only the count reaching 100 catches it.
  test("pages a deep grade's lots with load more (#465)", async ({ page, nav }) => {
    await nav.link("nav:stock").click();
    await page.getByRole("button", { name: tEn("stock:lotsButton") }).first().click();
    await expect(page.getByRole("heading", { name: tEn("stock:lotsHeading") })).toBeVisible();

    const lotRows = page.getByRole("button", { name: tEn("stock:historyButton"), exact: true });
    await expect(lotRows).toHaveCount(50);
    const loadMore = page.getByRole("button", { name: tEn("stock:loadMoreButton") });
    await expect(loadMore).toBeVisible();

    await loadMore.click();
    await expect(lotRows).toHaveCount(100);
    // ~176 lots in the fixture: still more to load after two pages.
    await expect(loadMore).toBeVisible();
  });

  test("is not offered the destinations it cannot use", async ({ nav }) => {
    for (const key of ["nav:customers", "nav:sales", "nav:audit", "nav:users", "nav:expenses"]) {
      await expect(
        nav.link(key),
        `the sidebar offered "${tEn(key as `nav:${string}`)}" to a ReadOnly user`,
      ).toBeHidden();
    }
    // The control: destinations it SHOULD have are present, so the assertion
    // above is proving a role gate rather than an empty/broken sidebar.
    await expect(nav.link("nav:stock")).toBeVisible();
    await expect(nav.link("nav:reports")).toBeVisible();
  });

  // The real boundary. Typing the URL is the attack, so the spec types the URL.
  for (const [route, headingKey] of [
    ["/audit", "audit:heading"],
    ["/users", "users:heading"],
  ] as const) {
    test(`is refused server-side on a direct link to ${route}`, async ({ page }) => {
      // Capture the API refusal itself. A visible error paragraph plus an absent
      // table is necessary but not sufficient: a 500, a malformed body, or the
      // API being down would satisfy both while saying nothing about
      // AUTHORIZATION, which is the guarantee this test is named for
      // (PR #390 review).
      const refusals: number[] = [];
      page.on("response", (res) => {
        if (/\/api\/v1\/(audit|users)/.test(res.url())) refusals.push(res.status());
      });

      await page.goto(route);

      // The screen itself renders — that is the documented design, not a bug —
      // so the guarantee is that it renders REFUSED and shows no data.
      await expect(page.getByRole("heading", { name: tEn(headingKey) })).toBeVisible();

      // Matched on the app's own error paragraph rather than on role="alert",
      // because THE TWO SCREENS DISAGREE and that disagreement is itself a
      // finding (#389): AuditPage renders `<p className="error" role="alert">`,
      // while UsersPage's load-failure branch renders a bare
      // `<p className="error">` with no role — so a screen-reader user is
      // refused in silence there. Asserting role="alert" here would have made
      // this spec a de-facto a11y test that fails on one screen for a reason
      // unrelated to the authorization boundary it is about. Assert the
      // refusal; #389 owns the announcement.
      await expect(
        page.locator("p.error"),
        `${route} rendered no error for a ReadOnly user — the server-side gate may be gone`,
      ).toBeVisible();

      // And, the part that actually matters: no rows leaked. An error banner
      // above a populated table would still be a data breach.
      await expect(
        page.getByRole("table"),
        `${route} rendered a data table for a ReadOnly user`,
      ).toHaveCount(0);

      // And the refusal was an authorization refusal.
      expect(refusals.length, `${route} made no API call at all`).toBeGreaterThan(0);
      expect(
        refusals,
        `${route} was refused with ${refusals.join(", ")} rather than 403 — the screen is empty `
          + `for some reason other than the authorization gate`,
      ).toContain(403);
    });
  }
});
