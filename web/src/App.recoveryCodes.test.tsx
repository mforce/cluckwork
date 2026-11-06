import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App } from "./App";
import { getTwoFactorStatus, listMyConnectedApps, regenerateRecoveryCodes } from "./api/cluckwork";
import { stepUp } from "./api/client";
import { askConsent } from "./api/oauth";
import { clearAccessToken } from "./auth/tokenStore";
import { setStoredToken } from "./test/jwt";
import { freshCode } from "./test/codes";
import i18n from "./i18n";

// #1277 review P3 (e), S4 (c) — App.tsx places the slot above the router and
// the shell: codes held there survive route changes inside the shell and a
// route outside it (/connect). SessionProvider is stubbed, so this proves the
// slot's place in App's tree, not the session bootstrap.
vi.mock("./session/SessionContext", async (importOriginal) => ({
  ...await importOriginal<typeof import("./session/SessionContext")>(),
  SessionProvider: ({ children }: { children: ReactNode }) => children,
}));
vi.mock("./pwa/UpdatePrompt", () => ({ UpdatePrompt: () => null }));
// The Connect page asks the server about a consent; here it waits forever, so
// the page is on screen, loading, whenever its lazy chunk has arrived.
vi.mock("./api/oauth", async (importOriginal) => ({
  ...await importOriginal<typeof import("./api/oauth")>(),
  askConsent: vi.fn(() => new Promise(() => undefined)),
}));
vi.mock("./api/cluckwork", async (importOriginal) => ({
  ...await importOriginal<typeof import("./api/cluckwork")>(),
  getTwoFactorStatus: vi.fn(),
  regenerateRecoveryCodes: vi.fn(),
  listMyConnectedApps: vi.fn(),
}));
vi.mock("./api/client", async (importOriginal) => ({
  ...await importOriginal<typeof import("./api/client")>(),
  stepUp: vi.fn(),
}));

const t = (key: string): string => i18n.t(`account:${key}` as "account:heading");

beforeEach(() => {
  vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: true, recoveryCodesLeft: 1 });
  vi.mocked(listMyConnectedApps).mockResolvedValue([]);
  vi.mocked(stepUp).mockResolvedValue({ token: crypto.randomUUID(), expiresAt: "2099-01-01T00:00:00Z" });
  setStoredToken({ sub: "u1", role: "Worker", two_factor: "true" });
  // The one request this tree makes that the mocks above do not answer; any
  // other is a failure, not a silent empty answer.
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    if (String(input) === "/api/v1/egg-unit-conversions")
      return new Response("[]", { status: 200, headers: { "Content-Type": "application/json" } });
    throw new Error(`Unexpected fetch: ${String(input)}`);
  }));
  window.history.replaceState(null, "", "/");
  window.history.pushState(null, "", "/account");
});

afterEach(() => {
  clearAccessToken();
  vi.clearAllMocks();
  vi.unstubAllGlobals();
});

async function holdRegeneratedCodes() {
  const codes = Array.from({ length: 10 }, () => crypto.randomUUID().slice(0, 19));
  vi.mocked(regenerateRecoveryCodes).mockResolvedValue({ recoveryCodes: codes });
  render(<App />);
  const regenerate = await screen.findByRole("button", { name: t("regenerateButton") }, { timeout: 5000 });
  await act(async () => { fireEvent.click(regenerate); });
  const proof = screen.getByRole("dialog");
  fireEvent.change(within(proof).getByLabelText(/Your current password/), { target: { value: crypto.randomUUID() } });
  fireEvent.change(within(proof).getByLabelText(/Authenticator or recovery code/), { target: { value: freshCode() } });
  await act(async () => { fireEvent.click(within(proof).getByRole("button", { name: t("regenerateButton") })); });
  await screen.findByRole("dialog", { name: t("codesHeading") });
  return codes;
}

const shownCodes = () =>
  within(screen.getByRole("dialog", { name: t("codesHeading") })).getAllByRole("listitem").map((li) => li.textContent);

const onlyExpectedFetches = () => {
  expect(vi.mocked(fetch).mock.calls.length).toBeGreaterThan(0);
  expect(vi.mocked(fetch).mock.calls.map(([input]) => String(input)).filter((url) => url !== "/api/v1/egg-unit-conversions")).toEqual([]);
};

describe("the app's own provider tree", () => {
  it("keeps unsaved recovery codes on screen through two route changes", async () => {
    const codes = await holdRegeneratedCodes();

    const rail = screen.getByRole("navigation", { name: i18n.t("nav:primaryNavAriaLabel"), hidden: true });
    await act(async () => { fireEvent.click(rail.querySelector('a[href="/help"]')!); });
    await waitFor(() => expect(window.location.pathname).toBe("/help"));
    await act(async () => { window.history.back(); });
    await waitFor(() => expect(window.location.pathname).toBe("/account"));

    expect(shownCodes()).toEqual(codes);
    onlyExpectedFetches();
  }, 20_000);

  it("keeps them on a route outside the shell, and back", async () => {
    const codes = await holdRegeneratedCodes();

    await act(async () => {
      window.history.pushState({ key: crypto.randomUUID() }, "", "/connect");
      window.dispatchEvent(new PopStateEvent("popstate", { state: window.history.state }));
    });
    await waitFor(() => expect(window.location.pathname).toBe("/connect"));
    // The Connect page itself, not only the route: the shell is gone and the
    // page is waiting on its consent request.
    await screen.findByText(i18n.t("connect:loading"), undefined, { timeout: 5000 });
    expect(askConsent).toHaveBeenCalled();
    expect(screen.queryByRole("navigation", { name: i18n.t("nav:primaryNavAriaLabel"), hidden: true })).toBeNull();
    expect(shownCodes()).toEqual(codes);
    await act(async () => { window.history.back(); });
    await waitFor(() => expect(window.location.pathname).toBe("/account"));

    expect(shownCodes()).toEqual(codes);
    onlyExpectedFetches();
  }, 20_000);
});
