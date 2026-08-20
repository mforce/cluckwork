// Login + in-memory access token + silent refresh across the 15-minute boundary.
//
// ================== HOW REFRESH ACTUALLY WORKS HERE ==================
//
// Read this before "fixing" the spec to wait for a timer. There ISN'T one. The
// SPA schedules NO proactive refresh — grep `web/src` and the only `setInterval`s
// are FarmContext's midnight day-rollover poll and the service-worker update
// check, neither of which touches tokens. The design is REACTIVE:
//
//   * `restoreSession()` runs ONCE at load (AuthContext's bootstrap).
//   * After that, a refresh happens only because a live request came back 401.
//     `apiGet`/`apiPost`/… catch it, call `refreshTokens()`, and retry once.
//   * `refreshTokens()` is single-flight per tab and cross-tab-serialised through
//     the Web Locks API, with a 15s timeout.
//
// So "silent refresh across the 15-minute boundary" is not observable by waiting
// for the app to do something at minute 15. It is observable by making a request
// AFTER the token has expired and watching the user not get bounced to /login.
//
// ================== THE TWO SPECS, AND WHY BOTH ==================
//
// Measured against the live stack: the access token's lifetime is exactly 15
// minutes (`exp - nbf`, decoded from a real login; the JWT carries `nbf`, not
// `iat`).
//
//   1. `forces a 401` — INJECTS a 401 into one API response. Runs always, ~2s.
//      It proves the refresh-and-retry path end to end, but it is a SIMULATION
//      of expiry: the token is still valid, the server never rejected it, and a
//      bug in how the app decides a token is stale could not be caught here.
//   2. `survives the real boundary` — waits out the real 15 minutes. Proves the
//      thing itself. Opt-in via CLUCKWORK_E2E_SLOW=1, because a suite that takes
//      sixteen minutes is a suite nobody runs.
//
// Naming which of the two ran is deliberate. A green "silent refresh" line in a
// report that only ever ran the injected version overstates what was verified,
// and that overstatement is exactly the kind this repo has been bitten by.

import { expect, test } from "../src/fixtures";
import { owner } from "../src/cast";
import {
  findRefreshCookie,
  REFRESH_COOKIE_NAME_PREFIX,
  RUN_SLOW_SPECS,
} from "../src/env";
import { tEn } from "../src/i18n";

/** Measured from a real login against the sim stack, not assumed. */
const ACCESS_TOKEN_LIFETIME_MS = 15 * 60 * 1000;

test.describe("Session", () => {
  test("the access token is never written to browser storage (#145)", async ({ page, signIn }) => {
    await signIn(owner());

    // #145's guarantee is that an XSS gets at most a 15-minute token, because
    // there is nothing durable to steal. Asserting the ABSENCE across both
    // stores, plus the legacy key the app actively purges, is the whole claim.
    const stored = await page.evaluate(() => ({
      local: Object.fromEntries(
        Object.keys(localStorage).map((k) => [k, localStorage.getItem(k) ?? ""]),
      ),
      session: Object.fromEntries(
        Object.keys(sessionStorage).map((k) => [k, sessionStorage.getItem(k) ?? ""]),
      ),
    }));

    const blob = JSON.stringify(stored);
    // A JWT is three base64url segments separated by dots and starts `eyJ`.
    // Matching the SHAPE rather than a key name catches a token stashed under
    // any name, which is what a regression would actually look like.
    expect(
      blob,
      `a JWT-shaped value is in browser storage — #145 says the access token lives only in memory. Storage: ${blob}`,
    ).not.toMatch(/eyJ[\w-]+\.[\w-]+\.[\w-]+/);
    expect(stored.local["cluckwork.tokens"], "the pre-#145 legacy token key is back").toBeUndefined();
  });

  test("the refresh cookie is HttpOnly and unreadable from the page", async ({ page, signIn }) => {
    await signIn(owner());

    // The other half of #145: the durable credential is a cookie the page cannot
    // touch. Read it from the BROWSER CONTEXT (which sees HttpOnly cookies) to
    // prove it exists, and from `document.cookie` (which must not) to prove it
    // is hidden — asserting only the second would pass if the cookie were simply
    // absent and the session broken in some other way.
    const cookies = await page.context().cookies();
    const refresh = findRefreshCookie(cookies);
    expect(refresh, "no refresh cookie was set at all").toBeTruthy();
    expect(refresh!.httpOnly, "the refresh cookie is readable by scripts").toBe(true);

    const visible = await page.evaluate(() => document.cookie);
    expect(visible, "the refresh cookie is exposed through document.cookie").not.toContain(
      REFRESH_COOKIE_NAME_PREFIX,
    );
  });

  test("forces a 401: the app refreshes and retries without bouncing the user to /login", async ({
    page,
    signIn,
    nav,
  }) => {
    await signIn(owner());

    // Inject a 401 into exactly ONE API response — the next one that is not the
    // refresh call itself. One-shot matters twice: the app's retry must be
    // allowed through (otherwise it is an infinite loop, not a test), and
    // refresh must never be the request that gets rejected (that would be
    // testing reuse-detection, a different guarantee).
    let injected = false;
    await page.route("**/api/v1/**", async (route) => {
      const url = route.request().url();
      if (injected || url.includes("/auth/refresh") || url.includes("/auth/login")) {
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

    const refreshed = page.waitForResponse(
      (r) => r.url().includes("/api/v1/auth/refresh") && r.request().method() === "POST",
    );

    await nav.link("nav:reports").click();

    // The app asked for a new token...
    const refreshResponse = await refreshed;
    expect(refreshResponse.status(), "the silent refresh itself failed").toBe(200);

    // ...and the USER never saw any of it. This is the guarantee: not "a refresh
    // request happened" (which a broken app that then logged the user out would
    // also satisfy) but "the screen they asked for is in front of them".
    await expect(page.getByRole("heading", { name: tEn("reports:title") })).toBeVisible();
    await expect(page).not.toHaveURL(/\/login/);
    await expect(page.getByRole("alert")).toBeHidden();
    expect(injected, "the 401 was never injected — this spec proved nothing").toBe(true);
  });

  test("survives the real 15-minute boundary", async ({ page, signIn, nav }) => {
    test.skip(
      !RUN_SLOW_SPECS,
      "real-clock spec: set CLUCKWORK_E2E_SLOW=1 to wait out the true 15-minute token lifetime",
    );
    // 15 minutes of waiting, plus slack for the navigation and refresh after it.
    test.setTimeout(ACCESS_TOKEN_LIFETIME_MS + 5 * 60 * 1000);

    await signIn(owner());

    // Idle past expiry. Nothing should happen during this window — there is no
    // proactive refresh — so the page simply sits there holding a token that
    // quietly goes stale, exactly as a barn phone left on a counter would.
    await page.waitForTimeout(ACCESS_TOKEN_LIFETIME_MS + 30_000);

    const refreshed = page.waitForResponse(
      (r) => r.url().includes("/api/v1/auth/refresh") && r.request().method() === "POST",
    );

    // The first real interaction after expiry. The server rejects the stale
    // token for real; nothing here is simulated.
    await nav.link("nav:reports").click();

    const refreshResponse = await refreshed;
    expect(refreshResponse.status()).toBe(200);
    await expect(page.getByRole("heading", { name: tEn("reports:title") })).toBeVisible();
    await expect(page).not.toHaveURL(/\/login/);
  });
});
