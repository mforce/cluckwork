import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App } from "./App";
import { getTwoFactorStatus, listMyConnectedApps, regenerateRecoveryCodes } from "./api/cluckwork";
import { stepUp } from "./api/client";
import { clearAccessToken } from "./auth/tokenStore";
import { setStoredToken } from "./test/jwt";
import { freshCode } from "./test/codes";
import i18n from "./i18n";

// #1277 review P3 (e) — the slot holds codes across route changes in the tree
// App.tsx really builds, not in a test's copy of it: keying or moving the
// provider under the router would remount it and drop the set.
vi.mock("./session/SessionContext", async (importOriginal) => ({
  ...await importOriginal<typeof import("./session/SessionContext")>(),
  SessionProvider: ({ children }: { children: ReactNode }) => children,
}));
vi.mock("./pwa/UpdatePrompt", () => ({ UpdatePrompt: () => null }));
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
  // Any request this test does not mock answers empty, not with the suite's
  // default 401, which would end the session.
  vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 204 })));
  window.history.replaceState(null, "", "/");
  window.history.pushState(null, "", "/account");
});

afterEach(() => {
  clearAccessToken();
  vi.clearAllMocks();
  vi.unstubAllGlobals();
});

describe("the app's own provider tree", () => {
  it("keeps unsaved recovery codes on screen through two route changes", async () => {
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

    const rail = screen.getByRole("navigation", { name: i18n.t("nav:primaryNavAriaLabel"), hidden: true });
    await act(async () => { fireEvent.click(rail.querySelector('a[href="/help"]')!); });
    await waitFor(() => expect(window.location.pathname).toBe("/help"));
    await act(async () => { window.history.back(); });
    await waitFor(() => expect(window.location.pathname).toBe("/account"));

    const dialog = screen.getByRole("dialog", { name: t("codesHeading") });
    expect(within(dialog).getAllByRole("listitem").map((li) => li.textContent)).toEqual(codes);
  }, 20_000);
});
