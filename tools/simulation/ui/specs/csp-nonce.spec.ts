// #873 — MUI's styles reach the screen under the real Production CSP.
//
// ================== WHY THIS SPEC EXISTS AT ALL ==================
//
// The defect it guards is invisible to every other instrument in this repo. The
// page renders, the DOM is correct, React is happy, and the `<style>` element
// Emotion injected is sitting in `<head>` — the browser simply refused to PARSE
// it, so `sheet === null` and not one declaration applies. Measured in #871:
// `html` computed `box-sizing: content-box`, `/daily-entry` laid out 419px wide
// in a 390px frame, and the only complaint anywhere was one console line.
//
// So the assertion has to be on a COMPUTED STYLE in a real browser under the
// real header. A unit test cannot see it (jsdom applies no policy), and an
// integration test cannot either (it reads bytes, and the bytes were always
// right).
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

const PROBE = '[data-testid="csp-style-probe"]';

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

/**
 * `--brand` as the engine resolves it, so the comparison below is colour to
 * colour rather than hex string to `rgb()`.
 *
 * Adds the rule through the CSSOM of a sheet the page already loaded, which is
 * the one styling instrument this policy permits — an injected `<style>` is the
 * very thing the policy blocks, and reaching for `addStyleTag` here would
 * measure nothing while looking like it worked (see src/mutants.ts, "THE THIRD
 * BOUNDARY").
 */
function brandColour(page: Page): Promise<string> {
  return page.evaluate(() => {
    const sheet = [...document.styleSheets].find((s) => s.href?.endsWith(".css"));
    if (!sheet) throw new Error("the SPA stylesheet has not loaded — nothing to read --brand from");
    sheet.insertRule(".csp-brand-reference { background-color: var(--brand); }", sheet.cssRules.length);
    const reference = document.createElement("div");
    reference.className = "csp-brand-reference";
    document.body.appendChild(reference);
    const colour = getComputedStyle(reference).backgroundColor;
    reference.remove();
    return colour;
  });
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
  test("MUI's themed background applies, and the page's nonce is its own response's", async ({ page }) => {
    const violations = cspViolations(page);

    const response = await page.goto(`${BASE_URL}/login`, { waitUntil: "domcontentloaded" });
    await page.getByRole("button", { name: tEn("auth:signIn") }).waitFor({ state: "visible" });

    // The document and the header that governs it were minted together.
    expect(await documentNonce(page)).toBe(headerNonce(response!.headers()["content-security-policy"]));

    const probe = page.locator(PROBE);
    await expect(probe, "the MUI probe is not on the login screen").toBeAttached();

    // The measurement. Without the nonce this is `rgba(0, 0, 0, 0)`: Emotion's
    // style element is present and refused, so the Chip has no background at
    // all. With it, the farm's own brand colour reaches an MUI component.
    const background = await probe.evaluate((el) => getComputedStyle(el).backgroundColor);
    expect(background, "MUI's injected styles did not apply — the nonce is not reaching Emotion")
      .not.toBe("rgba(0, 0, 0, 0)");
    expect(background, "MUI applied a background, but not the farm's").toBe(await brandColour(page));

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

      const background = await page.locator(PROBE)
        .evaluate((el) => getComputedStyle(el).backgroundColor);
      expect(background, "the cached shell lost its MUI styling offline")
        .toBe(await brandColour(page));

      expect(violations, "the browser refused something under the cached policy").toEqual([]);
    } finally {
      // Restore even if an assertion throws — a context left offline fails every
      // later spec in the run with an unrelated network error.
      await context.setOffline(false);
    }
  });
});
