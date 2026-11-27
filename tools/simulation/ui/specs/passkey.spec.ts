// specs/passkey.spec.ts — #1285 G8.8: passkey sign-in with Chrome's virtual
// authenticator, over the e2e job's TLS proxy (record 1282 decision 20).
//
// WebAuthn needs a secure context and a host name, so this spec runs at
// PASSKEY_BASE_URL, https://localhost:8495 in e2e-smoke.yml, where the
// passkeys-e2e overlay turns passkeys on. reset.sh registers the passkey of
// twoFactorOwner() there and the cast file carries its private key, which is
// loaded into the virtual authenticator.
//
// A conditional (autofill) request is answered only if the authenticator's
// presence simulation is on when the request starts. So every case loads the
// page with it off, and turns it on before the request it means to answer.
//
// Renewal: Playwright's page clock moves only the page, not the server, so the
// cases count the options requests rather than wait out a real ceremony.

import type { CDPSession, Page, Request } from "@playwright/test";
import { expect, submitSignIn, test } from "../src/fixtures";
import { owner, twoFactorOwner } from "../src/cast";
import { tEn } from "../src/i18n";

const PASSKEY_BASE_URL = process.env.PASSKEY_BASE_URL?.trim();
if (!PASSKEY_BASE_URL && process.env.CI)
  throw new Error("PASSKEY_BASE_URL is unset: e2e-smoke.yml must start the TLS proxy and set it.");

const OPTIONS = "/api/v1/auth/login/passkey/options";
const SIGN_IN = "/api/v1/auth/login/passkey";
const toBase64 = (b64url: string) => Buffer.from(b64url, "base64url").toString("base64");

test.use({ baseURL: PASSKEY_BASE_URL });
test.skip(!PASSKEY_BASE_URL, "PASSKEY_BASE_URL is unset; start the TLS proxy (tools/verify/features/mcp.md).");

interface Authenticator {
  cdp: CDPSession;
  id: string;
  presence: (on: boolean) => Promise<void>;
}

// The server refuses a sign count at or below the one it stored, as a cloned
// authenticator (G8.6), and each test makes a new virtual authenticator. So
// the count starts from the clock, above any earlier run's, and each test
// carries its last count to the next.
let signCount = Math.floor(Date.now() / 1000);
let current: Authenticator | null = null;

test.afterEach(async () => {
  if (!current) return;
  const { credentials } = await current.cdp.send("WebAuthn.getCredentials", { authenticatorId: current.id });
  signCount = Math.max(signCount, ...credentials.map((c) => c.signCount));
  current = null;
});

// The virtual authenticator, holding twoFactorOwner()'s passkey, with presence off.
async function authenticator(page: Page): Promise<Authenticator> {
  const passkey = twoFactorOwner().passkey;
  if (!passkey) throw new Error(".sim-cast.json has no twoFactorOwner.passkey. Re-run reset.sh with SIM_COMPOSE_OVERLAY set to the passkeys-e2e overlay.");
  const cdp = await page.context().newCDPSession(page);
  await cdp.send("WebAuthn.enable");
  const { authenticatorId: id } = await cdp.send("WebAuthn.addVirtualAuthenticator", {
    options: {
      protocol: "ctap2", transport: "internal", hasResidentKey: true, hasUserVerification: true,
      isUserVerified: true, automaticPresenceSimulation: false,
    },
  });
  await cdp.send("WebAuthn.addCredential", {
    authenticatorId: id,
    credential: {
      credentialId: toBase64(passkey.credentialId), isResidentCredential: true, rpId: passkey.rpId,
      privateKey: passkey.privateKey, userHandle: toBase64(passkey.userHandle), signCount,
    },
  });
  current = {
    cdp, id,
    presence: async (enabled) => {
      await cdp.send("WebAuthn.setAutomaticPresenceSimulation", { authenticatorId: id, enabled });
    },
  };
  return current;
}

// Every options request the page makes, in order.
function optionsRequests(page: Page): Request[] {
  const seen: Request[] = [];
  page.on("request", (r) => { if (r.method() === "POST" && new URL(r.url()).pathname === OPTIONS) seen.push(r); });
  return seen;
}

// Playwright reports a page's requests in the order the page makes them, so
// once it has seen this one it has seen every request made before it.
async function seenEveryRequest(page: Page) {
  const marker = page.waitForRequest((r) => new URL(r.url()).pathname === "/health/ready");
  await page.evaluate(() => fetch("/health/ready"));
  await marker;
}

async function setVisibility(page: Page, state: "visible" | "hidden") {
  await page.evaluate((s) => {
    Object.defineProperty(document, "visibilityState", { configurable: true, get: () => s });
    document.dispatchEvent(new Event("visibilitychange"));
  }, state);
}

// /reports opens its first report once signed in; either URL is the deep link.
const REPORTS = /\/reports(\/production)?$/;
const passkeyButton = (page: Page) => page.getByRole("button", { name: tEn("auth:passkeySignIn") });
const signedIn = (page: Page) => expect(page.locator("main#main-content")).toBeVisible();

test.describe("Passkey sign-in (#1285 G8.8)", () => {
  test("signs in from the button with a blank farm code, returns to the deep link and remembers the farm", async ({ page }) => {
    const auth = await authenticator(page);
    await page.goto("/reports");
    await expect(page).toHaveURL(/\/login$/);
    await expect(passkeyButton(page)).toBeEnabled();

    await auth.presence(true);
    await passkeyButton(page).click();

    await expect(page).toHaveURL(REPORTS);
    await signedIn(page);
    expect(await page.evaluate(() => localStorage.getItem("cluckwork.farmCodes"))).toContain("default-farm");
  });

  test("returns to a connected app's request after a passkey sign-in", async ({ page }) => {
    const auth = await authenticator(page);
    await page.goto("/connect?client_id=e2e-passkey-return&scope=farm%3Aread");
    await expect(page).toHaveURL(/\/login$/);
    await expect(passkeyButton(page)).toBeEnabled();

    await auth.presence(true);
    await passkeyButton(page).click();

    await expect(page).toHaveURL(/\/connect\?client_id=e2e-passkey-return&scope=farm%3Aread$/);
  });

  test("signs in through autofill in the email field", async ({ page }) => {
    const auth = await authenticator(page);
    await auth.presence(true);
    await page.goto("/reports");

    await expect(page).toHaveURL(REPORTS);
    await signedIn(page);
  });

  test("twoFactorOwner answers their password's second step with a passkey", async ({ page }) => {
    const auth = await authenticator(page);
    await page.goto("/reports");
    await expect(passkeyButton(page)).toBeEnabled();
    const member = twoFactorOwner();
    await page.getByLabel(tEn("auth:farmCode")).fill("default-farm");
    await page.getByLabel(tEn("auth:email")).fill(member.email);
    await page.getByLabel(tEn("auth:password")).fill(member.password);
    await page.getByRole("button", { name: tEn("auth:signIn"), exact: true }).click();

    await expect(page.getByRole("heading", { name: tEn("auth:passkeySecondTitle") })).toBeVisible();
    await auth.presence(true);
    const answered = page.waitForResponse((r) => new URL(r.url()).pathname === "/api/v1/auth/login/second-factor");
    await page.getByRole("button", { name: tEn("auth:passkeySecondSubmit") }).click();

    expect((await answered).status()).toBe(200);
    await expect(page).toHaveURL(REPORTS);
    await signedIn(page);
  });

  test("renews the autofill ceremony at 4 minutes, and the autofill sign-in posts the new one", async ({ page }) => {
    const auth = await authenticator(page);
    await page.clock.install();
    const requests = optionsRequests(page);
    await page.goto("/reports");
    await expect(passkeyButton(page)).toBeEnabled();
    expect(requests).toHaveLength(1);

    await auth.presence(true);
    const posted = page.waitForRequest((r) => new URL(r.url()).pathname === SIGN_IN);
    await page.clock.runFor("04:00");

    await expect.poll(() => requests.length).toBe(2);
    const second = await (await requests[1]!.response())!.json() as { ceremony: string };
    expect((await posted).postDataJSON().ceremony).toBe(second.ceremony);
    await signedIn(page);
  });

  test("asks for no ceremony while hidden, and exactly one on return once it is 4 minutes old", async ({ page }) => {
    await authenticator(page);
    await page.clock.install();
    const requests = optionsRequests(page);
    await page.goto("/login");
    await expect(passkeyButton(page)).toBeEnabled();

    await setVisibility(page, "hidden");
    await page.clock.runFor("04:30");
    await seenEveryRequest(page);
    expect(requests).toHaveLength(1);

    await setVisibility(page, "visible");
    await expect.poll(() => requests.length).toBe(2);
    await page.clock.runFor("00:30");
    await seenEveryRequest(page);
    expect(requests).toHaveLength(2);
  });

  test("a 429 from the options route stops autofill and disables the button, and the password still works", async ({ page }) => {
    const auth = await authenticator(page);
    // The e2e stack lifts this policy (decision 20); G8.9 covers the real one.
    await page.route(`**${OPTIONS}`, (route) => route.fulfill({
      status: 429, headers: { "Retry-After": "120" }, contentType: "application/problem+json",
      body: JSON.stringify({ title: "Too Many Requests", status: 429 }),
    }));
    const passkeySignIns: Request[] = [];
    page.on("request", (r) => { if (new URL(r.url()).pathname === SIGN_IN) passkeySignIns.push(r); });
    await page.goto("/reports");

    await expect(passkeyButton(page)).toBeDisabled();
    const note = tEn("auth:passkeyLimited_other", { count: 2 });
    await expect(passkeyButton(page)).toHaveAccessibleDescription(note);
    // The description is read even from a hidden element; a sighted user needs the visible note.
    await expect(page.locator("p", { hasText: note })).toBeVisible();
    await expect(page.getByLabel(tEn("auth:email"))).toHaveAttribute("autocomplete", "username");

    await auth.presence(true);
    await submitSignIn(page, owner());
    await expect(page).toHaveURL(REPORTS);
    await signedIn(page);
    expect(passkeySignIns).toHaveLength(0);
  });
});
