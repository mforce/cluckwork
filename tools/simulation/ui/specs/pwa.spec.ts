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
    // cached index.html. The health case is called out there as "verified — it
    // was". So each of those shapes is probed, from a CONTROLLED page.
    const probes = ["/health/live", "/api/v1/me", "/API/v1/me", "/api"];

    for (const path of probes) {
      const result = await page.evaluate(async (url) => {
        const res = await fetch(url);
        return { status: res.status, contentType: res.headers.get("content-type") };
      }, `${BASE_URL}${path}`);

      // The failure mode being caught is a 200 text/html — the SPA shell served
      // in place of the real response. Asserting a specific status per path would
      // couple this spec to auth details it is not about; asserting "not the
      // shell" is precisely the guarantee.
      expect(
        result.contentType ?? "",
        `${path} was answered with HTML — the navigation fallback is serving the cached shell `
          + `for a namespace that must always reach the network`,
      ).not.toContain("text/html");
    }

    // Control: the health endpoint really did reach the server and answer.
    const health = await page.evaluate(async (url) => {
      const res = await fetch(url);
      return { status: res.status, body: (await res.text()).slice(0, 32) };
    }, `${BASE_URL}/health/live`);
    expect(health.status).toBe(200);
    expect(health.body).toContain("Healthy");
  });
});
