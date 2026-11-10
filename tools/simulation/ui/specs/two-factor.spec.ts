// specs/two-factor.spec.ts — #1281 G6.5: an Owner with two-factor on signs in
// through the SPA's code step and still reaches the page they asked for.
//
// The code step replaces the password form in place, so the return path has to
// survive it. /connect relies on the same return path, but the stack serves
// plain HTTP and the authorization server refuses that outside Development, so
// the /connect half is the verify MCP client's (tools/verify/features/mcp.md).
//
// The deadline banner: the main simulation Owner stays unenrolled, and every
// reset.sh starts their grace afresh, so they are always inside it. The stack
// cannot put an Owner past the deadline (G6.8 forbids setting the days, and
// G6.7 forbids writing the start under tools/), so the setup screen is
// Vitest's (web/src/routes/TwoFactorEnrolmentPage.test.tsx).

import type { Page } from "@playwright/test";
import { expect, submitSignIn, test } from "../src/fixtures";
import { owner, twoFactorOwner } from "../src/cast";
import { tEn } from "../src/i18n";

const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const [before, after] = tEn("account:deadlineBanner", { deadline: "\u0000" }).split("\u0000");
const deadlineBanner = (page: Page) =>
  page.getByRole("note").filter({ hasText: new RegExp(`^${escape(before!)}.+${escape(after!)}`) });

test.describe("Two-factor sign-in (#1281)", () => {
  test("an Owner with two-factor on signs in with a code and lands on the page they asked for", async ({ page }) => {
    await page.goto("/reports");
    await expect(page).toHaveURL(/\/login$/);

    await submitSignIn(page, twoFactorOwner());

    await expect(page).toHaveURL(/\/reports$/);
    await expect(page.locator("main#main-content")).toBeVisible();
  });

  test("an Owner with two-factor on sees no deadline banner", async ({ page }) => {
    await page.goto("/reports");
    const status = page.waitForResponse((r) => r.url().endsWith("/api/v1/auth/two-factor") && r.ok());
    await submitSignIn(page, twoFactorOwner());
    await status;

    await expect(page.locator("main#main-content")).toBeVisible();
    await expect(deadlineBanner(page)).toHaveCount(0);
  });

  test("an Owner without two-factor sees the deadline, and Set up opens the setup on Account", async ({ page }) => {
    await page.goto("/");
    await submitSignIn(page, owner());

    const banner = deadlineBanner(page);
    await expect(banner).toBeVisible();
    await banner.getByRole("button", { name: tEn("account:setUpButton") }).click();

    await expect(page).toHaveURL(/\/account$/);
    await expect(page.getByRole("dialog", { name: tEn("account:setUpTitle") })).toBeVisible();
  });
});
