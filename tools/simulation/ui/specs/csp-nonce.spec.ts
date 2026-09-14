// #873 — the served document and its own Content-Security-Policy header carry
// the SAME style nonce, online and after a service-worker offline reload.
//
// ================== WHAT THIS SPEC DOES NOT COVER, AND WHY =================
//
// #873 originally asserted a COMPUTED STYLE here too — the farm's brand colour
// reaching an MUI component under the real Production CSP — through a hidden
// probe Chip on the login screen that existed only to be measured. The owner's
// #874 review decision (2026-09-14) removed that probe as production markup
// that exists only for a test: Login.tsx renders no MUI component today, and a
// hidden one is not a substitute for the real thing. That end-to-end assertion
// — an MUI component's computed style applies under the real CSP, online and
// offline — moves to the first screen slice that actually renders one; #864
// tracks it. Until then this spec proves the narrower, still load-bearing
// half: the nonce the document carries is the nonce its own header admits, and
// nothing the browser loads is CSP-refused.
//
// ================== THE OFFLINE HALF, AND WHY IT IS NOT REDUNDANT =========
//
// The nonce is per response, and the service worker (#142) precaches the shell
// and answers navigations from it. That is only safe because the cached entry is
// one whole Response: the `Content-Security-Policy` header and the `<meta>` in
// the body were minted together and are cached together, so they still agree
// when the network is gone. Nothing enforces that — it is a property of how
// workbox stores a response — which is exactly why it is asserted rather than
// assumed.

import { expect, test } from "../src/fixtures";
import type { Page } from "@playwright/test";
import { BASE_URL } from "../src/env";
import { tEn } from "../src/i18n";

/** The nonce the response's own policy admits. */
function headerNonce(csp: string | undefined): string {
  const match = /style-src 'self' 'nonce-([^']+)'/.exec(csp ?? "");
  expect(match?.[1], `no style-src nonce in the policy: ${csp}`).toBeTruthy();
  return match![1]!;
}

/** The nonce the served document handed to Emotion. */
function documentNonce(page: Page): Promise<string | null> {
  return page.evaluate(
    () => document.querySelector<HTMLMetaElement>('meta[name="csp-nonce"]')?.content ?? null,
  );
}

/** Records every CSP refusal the browser reports, for the whole test. */
function cspViolations(page: Page): string[] {
  const seen: string[] = [];
  page.on("console", (message) => {
    if (/content security policy/i.test(message.text())) seen.push(message.text());
  });
  return seen;
}

test.describe("CSP nonce (#873)", () => {
  test("the page's nonce is its own response's, and nothing is CSP-refused", async ({ page }) => {
    const violations = cspViolations(page);

    const response = await page.goto(`${BASE_URL}/login`, { waitUntil: "domcontentloaded" });
    await page.getByRole("button", { name: tEn("auth:signIn") }).waitFor({ state: "visible" });

    // The document and the header that governs it were minted together.
    expect(await documentNonce(page)).toBe(headerNonce(response!.headers()["content-security-policy"]));

    expect(violations, "the browser refused something under the policy").toEqual([]);
  });

  test("the cached shell keeps its nonce and its header together offline", async ({ page, context }) => {
    const violations = cspViolations(page);

    await page.goto(`${BASE_URL}/login`, { waitUntil: "domcontentloaded" });
    await page.waitForFunction(
      async () => (await navigator.serviceWorker.getRegistration("/"))?.active?.state === "activated",
      null,
      { timeout: 15_000 },
    );
    await page.evaluate(() => navigator.serviceWorker.ready.then(() => undefined));
    // Control arrives on the NEXT navigation (#142 registers without
    // clientsClaim), and an uncontrolled page has no worker to answer the
    // offline reload — without this the test would fail for the wrong reason.
    await page.reload();
    await page.waitForFunction(() => !!navigator.serviceWorker.controller, null, { timeout: 15_000 });

    await context.setOffline(true);
    try {
      const response = await page.reload({ waitUntil: "domcontentloaded" });
      await page.getByRole("button", { name: tEn("auth:signIn") }).waitFor({ state: "visible" });

      // Served by the worker from the precache, with no network behind it — and
      // still carrying the header it was cached with, naming the nonce its own
      // body carries.
      expect(await documentNonce(page))
        .toBe(headerNonce(response!.headers()["content-security-policy"]));

      expect(violations, "the browser refused something under the cached policy").toEqual([]);
    } finally {
      // Restore even if an assertion throws — a context left offline fails every
      // later spec in the run with an unrelated network error.
      await context.setOffline(false);
    }
  });
});
