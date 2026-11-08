// specs/two-factor.spec.ts — #1281 G6.5: an Owner with two-factor on signs in
// through the SPA's code step and still reaches the page they asked for.
//
// The code step replaces the password form in place, so the return path has to
// survive it. /connect relies on the same return path, but the stack serves
// plain HTTP and the authorization server refuses that outside Development, so
// the /connect half is the verify MCP client's (tools/verify/features/mcp.md).

import { expect, submitSignIn, test } from "../src/fixtures";
import { twoFactorOwner } from "../src/cast";

test.describe("Two-factor sign-in (#1281)", () => {
  test("an Owner with two-factor on signs in with a code and lands on the page they asked for", async ({ page }) => {
    await page.goto("/reports");
    await expect(page).toHaveURL(/\/login$/);

    await submitSignIn(page, twoFactorOwner());

    await expect(page).toHaveURL(/\/reports$/);
    await expect(page.locator("main#main-content")).toBeVisible();
  });
});
