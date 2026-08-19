// #310 acceptance — the two session-generation races, driven in a real browser.
//
// #310 is CLOSED. These two tests assert the OUTCOME of its races in a real
// browser — which session the user ends up in, and what a reload then does
// (post-#433: for the login race, a forced fresh sign-in — see the reload
// block's comment) — not the mechanism. The mechanism is `sessionGeneration` in
// `web/src/api/client.ts`: a module-level counter that `login()` and `logout()`
// bump, captured before each await and re-checked on settlement, so a superseded
// completion is discarded (`StaleSessionError`) instead of committing tokens.
//
// **These tests no longer exercise that counter at all**, and this file used to
// claim it was its end-to-end regression layer. See "WHAT THESE TESTS NOW COVER"
// below for what changed and what still stands behind them.
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
// `page.route` holds the refresh request open, so "in flight" is a state the
// spec controls rather than one it hopes to hit. The alternative — firing both
// actions and hoping the interleaving lands — is a spec that passes most of the
// time for the wrong reason, and this is precisely the class of bug where
// "passed 9 times out of 10" means "did not test it 9 times out of 10".
//
// **THE HOLD IS RELEASED BY AN EVENT, NOT BY A TIMER, AND THAT DISTINCTION IS
// THE WHOLE GUARANTEE.** An earlier version held for a fixed `HOLD_MS` measured
// from INTERCEPTION. But the thing the held response must outlive is the
// logout/login response, which is issued later and takes an unrelated amount of
// time — so the timer controlled the wrong interval. Measured against a build
// with the client-side fix removed, the superseded refresh completed `200` and
// applied its `Set-Cookie` BEFORE the login's, meaning the login's cookie was
// written last and won: the hazard never occurred and the spec passed with the
// bug fully present (PR #390 review round 3). Both races now release the hold
// only once the superseding response has actually landed.
//
// **That fixes the RELEASE ORDER, and it does not make the cookie hazard
// reproducible — do not read the paragraph above as claiming it does.** The
// section below explains why no release placement can, and it supersedes this
// one wherever they appear to disagree.
//
// `HOLD_MS` survives only as a post-release SETTLE window — time for the freed
// response to arrive and be acted on.
//
// ================== WHAT THESE TESTS NOW COVER, AND WHAT THEY DO NOT ==========
//
// **They no longer exercise `sessionGeneration`, and the file header above says
// they are its regression layer. Read this before trusting that.**
//
// `logout()` ABORTS the in-flight refresh, so on the logout path the superseded
// response is never delivered at all and the generation check that discards a
// late completion never runs there. Deleting `sessionGeneration` outright would
// still leave the logout test green — the abort's rejection surfaces as
// `AbortError`, which `isTransientRefreshFailure` already treats as transient
// (PR #390 review round 3).
//
// The login path has no such abort. One was added during this PR and then
// REVERTED (PR #390 review round 4): it introduced a spurious 401 for any
// request parked on the refresh when the login began, proven by a unit test
// whose fetch mock actually honours `AbortSignal`. The hazard it aimed at — a
// superseded refresh landing its `Set-Cookie` on top of a newer login's — was
// real, tracked as its own #310 follow-up (#393), and is now FIXED there —
// not by reattempting the abort, but by having `revokeSupersededCookie()`
// always revoke a stale-generation flight's cookie instead of skipping the
// revoke whenever an access token happens to be present (which never proved
// anything about which Set-Cookie the browser actually kept). See that
// function's own comment in `web/src/api/client.ts` and the corrected unit
// test in `client.test.ts`.
//
// **AND THE LATE-`Set-Cookie` RACE CANNOT BE EXPRESSED WITH THIS INSTRUMENT AT
// ALL.** This was chased to the bottom rather than assumed, and the answer is a
// property of Playwright, not of the app:
//
// `page.route` + a deferred `route.continue()` looks like it holds the OLD
// session's request open across a login. It does not. The request is re-issued
// when it is continued, and the browser re-attaches the cookie jar AS IT IS AT
// SEND TIME. `route.request().allHeaders()` keeps reporting the creation-time
// snapshot, which is what makes this so convincing from the outside — it printed
// the Owner cookie at both intercept and continue. But the response rotated the
// SALES chain, and the reload restored SALES. Measured on a build with the
// client-side abort REMOVED, i.e. the build where the bug is fully present:
//
//   [p] owner cookie              = 1qEdnjMO…(48)
//   [p] refresh hdr AT CONTINUE   = cluckwor…(61)   # 13 + 48: reads as Owner
//   [p] cookie after late refresh = SmmDlVsk…(46)   # neither Owner's nor Sales'
//   [p] statuses = login:200, refresh:200
//   [p] after reload: nav:audit count=0, nav:sales count=1   # Sales won
//
// So a request held and then `route.continue()`d always presents whatever cookie
// is current. Three placements of the release were tried; none changes this. Do
// not "fix" this spec by moving the release again.
//
// The mechanism, confirmed in `playwright-core@1.62.1` rather than inferred
// (PR #390 review round 4): `continue()` STRIPS any Cookie header —
// `headers: overrides.headers && removeCookieHeader(overrides.headers)` — and
// `types.d.ts` documents `Cookie` as a forbidden override that "will be
// ignored", pointing at `browserContext.addCookies` instead. Chromium then
// recomputes the header from the store when the request starts. `allHeaders()`
// keeps reporting the pause-time snapshot because it resolves from
// `setRawRequestHeaders(requestPausedEvent.request.headers)`, captured at
// request start. Hence the convincing-but-wrong reading.
//
// **SCOPE, stated precisely: this is a property of `route.continue()` under
// Playwright's CHROMIUM routing — not of Playwright generally, and not a proof
// that the race is untestable.** An earlier version of this comment said the
// race "cannot be expressed with this instrument at all". That was overstated,
// and the remedy it recommended (raw CDP `Fetch.continueRequest` with explicit
// headers) is the option LEAST likely to work, since it bypasses Playwright's
// stripping but not Chromium's own recomputation.
//
// The approach that should work, for whoever closes this: `route.fulfill()`
// DOES write `Set-Cookie` into the jar (Playwright splits multi-value
// `set-cookie` specifically for `Fetch.fulfillRequest`; Chromium only). So read
// the pre-login cookie via `context.cookies()` — this spec already does that —
// replay `/auth/refresh` out-of-band from a `request.newContext({ storageState })`
// seeded with it, and `fulfill({ response })` the held request with the result.
// `src/mutants.ts` already uses that `route.fetch()` + `fulfill({ response })`
// shape. Costed but not built here; recorded so the next attempt starts from a
// path that has a reason to succeed rather than from the one that does not.
//
// CONSEQUENCE, stated plainly (updated by #393): the hazard itself is fixed
// and unit-covered in `client.test.ts` now — see revokeSupersededCookie()'s
// comment in `web/src/api/client.ts`. What's still true is narrower than the
// original claim: THIS spec, specifically, still cannot express the cookie
// mechanism at the browser/E2E level, because `page.route` + `route.continue()`
// cannot carry a stale cookie (see above) — that's a property of this testing
// instrument, not of whether the hazard is fixed. The `route.fulfill()` replay
// approach above remains the path for whoever wants E2E-level proof of the
// cookie mechanism itself; costed but still not built. This spec asserts the
// OUTCOME of the race (which session the user lands in, and what a reload
// then does); it was never evidence about the cookie mechanism, fixed or not.
//
// What these two tests DO cover, and what the killed `logout-not-honoured`
// mutant stands behind: the session the user ends up in after the race, and
// what a reload then does. Post-#433 those differ per race: the logout race's
// reload stays signed out, and the login race's reload asks for a fresh
// sign-in (the unconditional stale-flight revoke — #393/#433/#455 — has
// killed the jar's cookie by then, on purpose). That is still the outcome a
// farmer would notice.

import { expect, test } from "../src/fixtures";
import { castMember, owner } from "../src/cast";
import { tEn } from "../src/i18n";

/**
 * Post-release SETTLE window — time for the freed refresh to arrive and be acted
 * on. It does NOT order anything and no longer governs how long a request is
 * held: the hold is released by an event (see the header).
 */
const HOLD_MS = 3_000;

test.describe("#310 session races", () => {
  test("a logout during an in-flight refresh cannot be resurrected by the late response", async ({
    page,
    signIn,
    nav,
  }) => {
    await signIn(owner());

    // Hold every refresh open until the LOGOUT response has landed — see the
    // header note. A fixed timer here let the refresh finish first, in which
    // case logout revokes an already-rotated token and the test passes without
    // ever producing the late-response ordering it is named for.
    let releaseHeldRefresh!: () => void;
    const logoutHasLanded = new Promise<void>((resolve) => {
      releaseHeldRefresh = resolve;
    });
    await page.route("**/api/v1/auth/refresh", async (route) => {
      await logoutHasLanded;
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

    // The user signs out mid-flight. Wait for the logout RESPONSE specifically —
    // that is the event whose Set-Cookie the held refresh must be made to
    // outlive.
    const logoutLanded = page.waitForResponse(
      (r) => r.url().includes("/api/v1/auth/logout") && r.request().method() === "POST",
    );
    // Release in a `finally`, immediately after the logout response — same two
    // reasons as the login race below: an assertion placed before the release
    // can strand the interception on failure, and holding past the superseding
    // response can stop the mutant build from ever reaching the guarantee.
    try {
      await nav.signOut.click();
      await logoutLanded;
    } finally {
      releaseHeldRefresh();
    }
    await expect(page).toHaveURL(/\/login/);

    // Give the freed refresh time to arrive and be acted on.
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

    // Held until the LOGIN response has landed — see the header note. This is
    // the ordering the guarantee is about, and a fixed timer did not produce it.
    let releaseHeldRefresh!: () => void;
    const loginHasLanded = new Promise<void>((resolve) => {
      releaseHeldRefresh = resolve;
    });
    await page.route("**/api/v1/auth/refresh", async (route) => {
      await loginHasLanded;
      await route.continue();
    });

    // Land on /login with that cookie present: AuthContext's bootstrap fires
    // `restoreSession()` immediately, and it is now held open. /login is outside
    // ProtectedRoute, so the form is interactive while that is pending.
    //
    // #428 — goto() and waitForRequest() MUST be raced together, not sequential.
    // goto()'s default waitUntil:"load" doesn't resolve until every subresource
    // (this page fetches 7 web fonts) finishes, and the bootstrap fetch fires as
    // soon as React mounts — often before that. A listener registered after goto()
    // resolves can miss a request that already happened, which is exactly what
    // made this racy: reliable locally (fast asset loads racing the listener into
    // place in time) and a deterministic 45s timeout on CI's slower bundled
    // Chromium, where the fetch — and its cancellation once orphaned mid-navigation
    // — both happen before the sequential await ever got here.
    await Promise.all([
      page.waitForRequest((r) => r.url().includes("/api/v1/auth/refresh")),
      page.goto("/login"),
    ]);

    // A DIFFERENT person signs in on the same browser while the old session's
    // refresh is still outstanding. Different persona on purpose: the two
    // sessions are then distinguishable on screen, so the assertion can name
    // which one won instead of inferring it.
    const sales = castMember("Sales");
    await page.getByLabel(tEn("auth:farmCode")).fill("default-farm");
    await page.getByLabel(tEn("auth:email")).fill(sales.email);
    await page.getByLabel(tEn("auth:password")).fill(sales.password);
    const loginLanded = page.waitForResponse(
      (r) => r.url().includes("/api/v1/auth/login") && r.request().method() === "POST",
    );
    // RELEASE THE INSTANT THE LOGIN RESPONSE LANDS — before any assertion, and
    // in a `finally`. Both halves are load-bearing, and each fixes a separate
    // defect found in round 3's own first attempt:
    //
    //   * BEFORE the nav assertion, and this is REQUIRED, not a preference.
    //     Nothing on the login path cancels the held bootstrap refresh, so while
    //     it is held AuthContext's `isLoading` stays set and `Login.tsx` never
    //     navigates — the nav simply is not there to assert on. Releasing here
    //     is what lets the shell render at all.
    //
    //     (Precisely, on the timing: the refresh does eventually self-abort on
    //     `REFRESH_TIMEOUT_MS = 15_000`, so `isLoading` is not stuck forever —
    //     but the `expect` timeout is 10s, so the assertion loses that race. An
    //     earlier comment here said `isLoading` "never clears", which is wrong
    //     and is the kind of detail that gets re-derived incorrectly.)
    //
    //     The first version released three lines later, after asserting the
    //     shell had rendered. Against a build carrying the since-reverted login
    //     abort, that ordering made a mutant die on the nav timeout rather than
    //     on the guarantee, and "the mutant failed" was read as "the guarantee
    //     is covered" — a red for the wrong reason, which is exactly what the
    //     mutation harness exists to refuse (PR #390 review rounds 3 and 4).
    //   * In a `finally`. The route handler awaits this promise and nothing else
    //     resolves it, so an assertion throwing before the release strands the
    //     interception and buries the real error under a timeout.
    //
    // Releasing here still satisfies the ordering the test is named for: the
    // login response has landed, so its `Set-Cookie` is already applied, and the
    // freed refresh can only write on top of it.
    // Registered BEFORE the click (so before the release in the finally
    // below): the freed stale refresh settles, and the client's reaction to
    // discarding it is the unconditional #393/#433 cookie revoke — a POST to
    // /auth/logout. Awaiting BOTH, instead of the fixed settle timer this
    // used to sleep, is what makes the reload below deterministic on a loaded
    // runner too: a timer that usually covers the two round-trips races them
    // on a slow PR runner, and this suite now gates covered PRs (codex review
    // of #456).
    const staleRefreshSettled = page.waitForResponse(
      (r) => r.url().includes("/api/v1/auth/refresh"),
    );
    const revokeSettled = page.waitForResponse(
      (r) => r.url().includes("/api/v1/auth/logout"),
    );
    try {
      await page.getByRole("button", { name: tEn("auth:signIn") }).click();
      await loginLanded;
    } finally {
      releaseHeldRefresh();
    }

    await expect(page.getByRole("navigation", { name: tEn("nav:primaryNavAriaLabel") }))
      .toBeVisible();

    // The freed refresh has arrived, been discarded as stale, and its cookie
    // revoke has completed — everything the reload's outcome depends on.
    await staleRefreshSettled;
    await revokeSettled;

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

    // Reload to pin the DURABLE half of the contract — which CHANGED in
    // #393/#433. A discarded stale flight's cookie is now revoked
    // UNCONDITIONALLY: network arrival order decides which Set-Cookie the
    // browser kept, JS cannot observe it, and the HttpOnly cookie can't be
    // read back — so the revoke deliberately accepts catching the newer
    // session's own cookie rather than ever leaving the PREVIOUS user's
    // cookie live (the wrong-session hole #393 closed). In the race this
    // test manufactures, that worst case is certain: the freed Owner
    // refresh is always discarded as stale, so the jar's cookie — whichever
    // of the two it is — is dead by the time of this reload.
    //
    // The documented outcome (Help → Signing in, multi-tab resync; #455) is
    // a forced fresh sign-in, and that is the SAFE outcome this asserts: the
    // reload restores NOBODY — not the old Owner session, and not a Sales
    // session riding a cookie the client no longer trusts. Before #433 this
    // block asserted the Sales session survived the reload; that assertion
    // described the pre-#433 contract and broke the day the fix merged
    // (silently — this suite is workflow_dispatch-only, the #370 trap).
    await page.reload();
    await expect(page.getByLabel(tEn("auth:email"))).toBeVisible();

    // And the forced re-auth is a plain re-auth, not a wedged account:
    // Sales signs straight back in and lands in a Sales-shaped shell.
    await page.getByLabel(tEn("auth:farmCode")).fill("default-farm");
    await page.getByLabel(tEn("auth:email")).fill(sales.email);
    await page.getByLabel(tEn("auth:password")).fill(sales.password);
    await page.getByRole("button", { name: tEn("auth:signIn") }).click();
    await expect(nav.link("nav:sales")).toBeVisible();
    await expect(nav.link("nav:audit")).toBeHidden();
  });
});
