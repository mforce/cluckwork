// #310 acceptance — the two session-generation races, driven in a real browser.
//
// #310 is CLOSED; this is the end-to-end regression layer for it, not the fix.
// The fix is `sessionGeneration` in `web/src/api/client.ts`: a module-level
// counter that `login()` and `logout()` bump, captured before each await and
// re-checked on settlement, so a superseded completion is discarded
// (`StaleSessionError`) instead of committing tokens.
//
// ================== WHY A BROWSER IS THE RIGHT INSTRUMENT ==================
//
// Both races are about what happens to a REAL in-flight fetch when the user does
// something else mid-flight. A unit test can assert the counter's arithmetic; it
// cannot tell you whether the user ends up looking at somebody else's farm. The
// assertions below are therefore all about the resulting SCREEN — which session
// the browser is actually in — not about a counter or a request.
//
// ================== HOW THE RACE IS MADE DETERMINISTIC ==================
//
// `page.route` holds the refresh request open for a fixed window, so "in flight"
// is a state the spec controls rather than one it hopes to hit. The alternative,
// firing both actions and hoping the interleaving lands, is a spec that passes
// most of the time for the wrong reason — and this is precisely the class of bug
// where "passed 9 times out of 10" means "did not test it 9 times out of 10".

import { expect, test } from "../src/fixtures";
import { castMember, owner } from "../src/cast";
import { tEn } from "../src/i18n";

/** How long a held refresh stays in flight. Long enough to act inside, short enough to run. */
const HOLD_MS = 3_000;

test.describe("#310 session races", () => {
  test("a logout during an in-flight refresh cannot be resurrected by the late response", async ({
    page,
    signIn,
    nav,
  }) => {
    await signIn(owner());

    // Hold every refresh open for HOLD_MS.
    await page.route("**/api/v1/auth/refresh", async (route) => {
      await new Promise((r) => setTimeout(r, HOLD_MS));
      await route.continue();
    });

    // Provoke a refresh by injecting one 401 (same one-shot technique as
    // session-refresh.spec.ts). The refresh it triggers is now held open.
    let injected = false;
    await page.route("**/api/v1/**", async (route) => {
      const url = route.request().url();
      if (injected || url.includes("/auth/")) {
        await route.fallback();
        return;
      }
      injected = true;
      await route.fulfill({
        status: 401,
        contentType: "application/problem+json",
        body: JSON.stringify({ title: "Unauthorized", status: 401 }),
      });
    });

    await nav.link("nav:reports").click();
    // Do NOT await the refresh here — the whole point is to act while it is
    // still outstanding.
    await page.waitForRequest((r) => r.url().includes("/api/v1/auth/refresh"));

    // The user signs out mid-flight.
    await nav.signOut.click();
    await expect(page).toHaveURL(/\/login/);

    // Now let the held refresh land, and then some.
    await page.waitForTimeout(HOLD_MS + 2_000);

    // THE GUARANTEE, in three independent forms — because "we are on /login" on
    // its own would be satisfied by a router that moved while the session
    // quietly survived underneath it.
    //
    // 1. Still on the login screen, not swept back into the app.
    await expect(page).toHaveURL(/\/login/);
    await expect(page.getByRole("navigation", { name: tEn("nav:primaryNavAriaLabel") }))
      .toBeHidden();

    // 2. The credential itself is gone. If the late refresh had committed, its
    //    rotated cookie would be sitting here ready to restore the session.
    const cookies = await page.context().cookies();
    const refreshCookie = cookies.find((c) => c.name === "cluckwork_rt" && c.value !== "");
    expect(
      refreshCookie,
      "a live refresh cookie survived the logout — the late response resurrected the session",
    ).toBeUndefined();

    // 3. The strongest form: ask for a protected screen and be refused. This is
    //    what an attacker with the abandoned tab would actually try.
    await page.goto("/");
    await expect(
      page,
      "a protected route was served after logout — the session outlived the sign-out",
    ).toHaveURL(/\/login/);
  });

  test("an explicit login beats a pending bootstrap refresh, and the newer login wins", async ({
    page,
    signIn,
    nav,
  }) => {
    // Establish a real Owner session first, so the browser is holding a VALID
    // refresh cookie — the bootstrap refresh this race needs must be one that
    // would genuinely succeed. A race against a doomed request proves nothing.
    await signIn(owner());

    await page.route("**/api/v1/auth/refresh", async (route) => {
      await new Promise((r) => setTimeout(r, HOLD_MS));
      await route.continue();
    });

    // Land on /login with that cookie present: AuthContext's bootstrap fires
    // `restoreSession()` immediately, and it is now held open. /login is outside
    // ProtectedRoute, so the form is interactive while that is pending.
    await page.goto("/login");
    await page.waitForRequest((r) => r.url().includes("/api/v1/auth/refresh"));

    // A DIFFERENT person signs in on the same browser while the old session's
    // refresh is still outstanding. Different persona on purpose: the two
    // sessions are then distinguishable on screen, so the assertion can name
    // which one won instead of inferring it.
    const sales = castMember("Sales");
    await page.getByLabel(tEn("auth:email")).fill(sales.email);
    await page.getByLabel(tEn("auth:password")).fill(sales.password);
    await page.getByRole("button", { name: tEn("auth:signIn") }).click();

    await expect(page.getByRole("navigation", { name: tEn("nav:primaryNavAriaLabel") }))
      .toBeVisible();

    // Let the superseded bootstrap refresh land on top of the new session.
    await page.waitForTimeout(HOLD_MS + 2_000);

    // THE GUARANTEE: this is the Sales session, and the Owner's has not been
    // restored underneath it. Asserted through the nav's role gates, which
    // differ between the two personas — Sales is not an admin, so the Setup
    // group is absent, and Sales cannot produce, so the Production group is too.
    await expect(
      nav.link("nav:audit"),
      "an admin destination appeared — the late Owner refresh overwrote the Sales login",
    ).toBeHidden();
    await expect(nav.link("nav:users")).toBeHidden();
    await expect(nav.link("nav:expenses")).toBeHidden();
    await expect(nav.link("nav:dailyEntry")).toBeHidden();

    // Control: the Sales-shaped destinations ARE there, so the assertions above
    // are proving "the Sales session won" rather than "the sidebar is empty".
    await expect(nav.link("nav:sales")).toBeVisible();
    await expect(nav.link("nav:customers")).toBeVisible();
  });
});
