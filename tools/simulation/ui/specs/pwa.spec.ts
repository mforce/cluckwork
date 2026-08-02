// The PWA shell (#277 cross-cutting, #142).
//
// ================== WHAT IS COVERED, AND WHAT IS NOT ==================
//
// #277 lists "the PWA update prompt path" as a cross-cutting item. **The update
// prompt itself is NOT covered here, and the reason is a measured limitation of
// the instrument rather than a decision to skip it.**
//
// To render `UpdatePrompt`, a byte-different `sw.js` has to install and park in
// `waiting`. The app provokes that with `registration.update()` on an hour-long
// interval (`UPDATE_CHECK_INTERVAL_MS = 60 * 60_000`), so the honest way to force
// it is to serve different bytes and call `update()` by hand. That does not work
// from Playwright, and it was established by probe, not assumed:
//
//   * `context.route("**/sw.js")` DOES intercept the initial registration fetch
//     (observed: one interception, and `context.on("request")` reports exactly
//     one `sw.js` request, `resourceType: "script"`).
//   * After `registration.update()`, the interception counter does NOT move and
//     NO further `sw.js` request is visible to Playwright at all — the update
//     fetch happens outside what the context can see or serve. Tried in both
//     directions (serve-mutated-then-real, and serve-real-then-mutated); in each
//     case `registration.waiting` stayed `null` and no prompt appeared.
//   * `/sw.js` is served `Cache-Control: no-cache` with an ETag, so this is not
//     a caching artefact on the server side.
//
// So the waiting-worker -> prompt path has no end-to-end coverage. What it does
// have is `web/src/pwa/UpdatePrompt.test.tsx` (the component, given an `activate`
// callback) and `web/src/pwa/registerServiceWorker.test.ts` (the registration and
// update-detection logic). Between them the LOGIC is covered; what nothing
// covers is the browser genuinely parking a second worker — which is exactly the
// property `registerType: "prompt"` exists to guarantee, and the one that would
// break if somebody switched on `skipWaiting`.
//
// That gap is recorded in docs/superpowers/notes/277-decisions.md rather than
// papered over with a test that asserts something adjacent and calls it done.
//
// What IS covered below is the rest of #142's promise, all of it end-to-end:
// the worker installs and takes control, the shell survives offline, and the
// denylist keeps the API and health namespaces off the cached shell.

import { expect, test } from "../src/fixtures";
import { owner } from "../src/cast";
import { BASE_URL } from "../src/env";
import { tEn } from "../src/i18n";

test.describe("PWA shell", () => {
  test("the service worker installs and takes control of the page", async ({ page, signIn }) => {
    await signIn(owner());

    await page.waitForFunction(
      async () => (await navigator.serviceWorker.getRegistration("/"))?.active?.state === "activated",
      null,
      { timeout: 15_000 },
    );

    // Control arrives on the NEXT navigation, not on activation — #142 registers
    // without `clientsClaim`, so the page that installed the worker deliberately
    // finishes its life uncontrolled. Asserting control before a reload would be
    // asserting a behaviour the app intentionally does not have.
    expect(
      await page.evaluate(() => !!navigator.serviceWorker.controller),
      "the installing page was claimed immediately — clientsClaim may have been switched on",
    ).toBe(false);

    await page.reload();
    await page.waitForFunction(() => !!navigator.serviceWorker.controller, null, { timeout: 15_000 });

    const reg = await page.evaluate(async () => {
      const r = await navigator.serviceWorker.getRegistration("/");
      return { scope: r?.scope ?? null, script: r?.active?.scriptURL ?? null };
    });
    expect(reg.scope, "the worker's scope is not the whole app").toBe(`${BASE_URL}/`);
    expect(reg.script).toBe(`${BASE_URL}/sw.js`);
  });

  test("the app shell still loads with the network down", async ({ page, context, signIn }) => {
    await signIn(owner());
    await page.waitForFunction(
      async () => (await navigator.serviceWorker.getRegistration("/"))?.active?.state === "activated",
      null,
      { timeout: 15_000 },
    );
    // Take control first — an uncontrolled page has no worker to answer the
    // offline navigation, so without this the spec would be testing nothing and
    // failing for the wrong reason.
    await page.reload();
    await page.waitForFunction(() => !!navigator.serviceWorker.controller, null, { timeout: 15_000 });

    await context.setOffline(true);
    try {
      await page.reload({ waitUntil: "domcontentloaded" });

      // THE GUARANTEE (#142): the app is launchable from a home screen and
      // survivable on a bad connection. So it must PAINT — not show the
      // browser's offline error page.
      await expect(page).toHaveTitle("Cluckwork");
      await expect(
        page.getByRole("heading", { name: tEn("auth:title") }),
        "the shell did not render offline — the precache is not answering navigations",
      ).toBeVisible();

      // It lands on the LOGIN screen, and that is correct rather than a
      // shortcoming: the access token lives only in memory (#145) so a reload
      // wipes it, and restoring the session needs a network call that cannot
      // happen. #142 caches the app SHELL and no application data — offline data
      // capture is #50, deliberately not this.
      await expect(page).toHaveURL(/\/login/);
    } finally {
      // Restore even if an assertion throws — a context left offline would fail
      // every later spec in this file with an unrelated network error.
      await context.setOffline(false);
    }
  });

  test("the service worker never answers /api or /health from the cached shell", async ({
    page,
    signIn,
  }) => {
    await signIn(owner());
    await page.waitForFunction(
      async () => (await navigator.serviceWorker.getRegistration("/"))?.active?.state === "activated",
      null,
      { timeout: 15_000 },
    );
    await page.reload();
    await page.waitForFunction(() => !!navigator.serviceWorker.controller, null, { timeout: 15_000 });

    // `navigateFallbackDenylist` is `[/^\/api(?:[/?]|$)/i, /^\/health(?:[/?]|$)/i]`,
    // and the vite config records that the naive `/^\/api\//` version was a real
    // bug — it missed a bare `/api`, a query-only `/api?x=1`, and (since ASP.NET
    // routing is case-insensitive) `/API/v1/...`, all of which were then handed a
    // cached index.html. The health case is called out there as "verified — it was".
    //
    // THESE MUST BE NAVIGATIONS, NOT `fetch()`. An earlier version of this spec
    // used `page.evaluate(fetch)`, which proved nothing: `navigateFallback` only
    // applies to requests whose mode is `navigate`, so an ordinary fetch bypasses
    // the fallback whether or not the denylist exists. Deleting the denylist
    // entirely left that version green (PR #390 review). `page.goto()` issues a
    // real navigation, which is the only request type the rule governs.
    const probes = ["/health/live", "/api/v1/me", "/API/v1/me", "/api"];

    // Record what the SERVER actually answered for each probe. A navigation
    // error alone is not proof: `chrome-error://chromewebdata` is Chromium's own
    // error document and it appears for a refused connection too, so a stack
    // that was simply DOWN would have read as a green denylist guarantee
    // (PR #390 review round 2). Requiring a real response with a non-HTML
    // content-type distinguishes "the server refused this" from "nothing
    // answered".
    const serverAnswers = new Map<string, string>();
    page.on("response", (res) => {
      const url = new URL(res.url());
      if (url.origin === new URL(BASE_URL).origin) {
        serverAnswers.set(url.pathname, res.headers()["content-type"] ?? "");
      }
    });

    for (const path of probes) {
      // A navigation to a non-2xx JSON endpoint makes Chromium raise
      // ERR_HTTP_RESPONSE_CODE_FAILURE rather than resolving — and that throw is
      // itself the proof this spec wants: the browser got the SERVER's error, not
      // a cached 200 HTML shell. If the denylist were removed, the SW would
      // answer these navigations from the precache and `goto` would resolve
      // happily with `text/html`.
      let response: Awaited<ReturnType<typeof page.goto>> | null = null;
      let navigationError: string | null = null;
      try {
        response = await page.goto(`${BASE_URL}${path}`, { waitUntil: "commit" });
      } catch (err) {
        navigationError = (err as Error).message;
      }

      if (navigationError) {
        expect(
          navigationError,
          `${path} failed to navigate for an unexpected reason`,
          // ERR_ABORTED is deliberately NOT accepted: it is also the signature
          // of the "interrupted by another navigation" harness race that the
          // settle step below exists to prevent, so accepting it would let a
          // slow CI run mask a genuinely broken denylist (PR #390 review
          // round 2). If it ever appears, that is a harness fault to fix, not a
          // result to pass.
        ).toMatch(/ERR_HTTP_RESPONSE_CODE_FAILURE|ERR_INVALID_RESPONSE|chrome-error:\/\/chromewebdata/);

        // ...and the server genuinely answered, with something that is not the
        // shell. Without this, a down backend passes.
        const answered = serverAnswers.get(new URL(`${BASE_URL}${path}`).pathname);
        expect(
          answered,
          `${path} produced a navigation error but no server response — the stack may be down, `
            + `which is not evidence about the denylist`,
        ).toBeDefined();
        expect(answered ?? "", `${path} was answered with HTML`).not.toContain("text/html");
        // Settle on a real page before the next probe. A navigation that ended
        // in Chromium's error document leaves the tab mid-flight, and the very
        // next `goto` reports "interrupted by another navigation" — a harness
        // artefact that reads exactly like a product failure.
        // Settle on a REAL quiescence signal, not a lifecycle event. Neither
        // `commit` nor `domcontentloaded` was enough: the SPA boots and then
        // routes client-side, so a fresh navigation was still starting when the
        // next probe began and interrupted it. Waiting for the login form to be
        // on screen means React has finished routing and nothing further is
        // pending.
        await page.goto(`${BASE_URL}/login`, { waitUntil: "domcontentloaded" }).catch(() => {});
        await page
          .getByRole("button", { name: tEn("auth:signIn") })
          .waitFor({ state: "visible", timeout: 15_000 })
          .catch(() => {});
        continue;
      }

      expect(response, `${path} produced no response at all`).not.toBeNull();
      const contentType = response!.headers()["content-type"] ?? "";
      expect(
        contentType,
        `${path} was answered with HTML — the navigation fallback is serving the cached shell `
          + `for a namespace that must always reach the network`,
      ).not.toContain("text/html");

      const body = await response!.text().catch(() => "");
      expect(body.slice(0, 400), `${path} returned the SPA document body`)
        .not.toContain('<div id="root"');
    }

    // Control: the health endpoint really did reach the server and answer, so the
    // loop above is proving "not the shell" rather than "nothing works".
    const health = await page.goto(`${BASE_URL}/health/live`, { waitUntil: "commit" });
    expect(health!.status()).toBe(200);
    expect(health!.headers()["content-type"] ?? "").not.toContain("text/html");
    expect(await health!.text()).toContain("Healthy");
  });
});
