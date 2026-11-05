import { describe, it, expect, vi, beforeEach } from "vitest";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { BrowserRouter, Route, Routes } from "react-router";
import { AccountPage } from "../AccountPage";
import { AppLayout } from "../AppLayout";
import { renderWithProviders } from "../../test/renderWithProviders";
import { farmState } from "../../test/fixtures";
import { freshCode } from "../../test/codes";
import { setStoredToken } from "../../test/jwt";
import { AuthProvider } from "../../auth/AuthContext";
import { FarmContext } from "../../farm/FarmContext";
import { MeContext } from "../../session/SessionContext";
import { getTwoFactorStatus, listMyConnectedApps, startTwoFactor } from "../../api/cluckwork";
import { confirmTwoFactor, stepUp } from "../../api/client";
import i18n from "../../i18n";

// #1277 MC3 (maintainer, 2026-11-05: "Block both"): nothing leaves the page
// while confirm is in flight, and Back does not drop recovery codes that are
// not saved yet. Closing the tab is left to beforeunload.
vi.mock("../../api/cluckwork", async (importOriginal) => ({
  ...await importOriginal<typeof import("../../api/cluckwork")>(),
  getTwoFactorStatus: vi.fn(),
  startTwoFactor: vi.fn(),
  listMyConnectedApps: vi.fn(),
}));
vi.mock("../../api/client", async (importOriginal) => ({
  ...await importOriginal<typeof import("../../api/client")>(),
  stepUp: vi.fn(),
  confirmTwoFactor: vi.fn(),
  logout: vi.fn(),
  setOnTokensChanged: vi.fn(),
  setOnUnauthenticated: vi.fn(),
}));

const t = (key: string): string => i18n.t(`account:${key}` as "account:heading");
const EMAIL = "test@farm.local";
const freshKey = () => crypto.randomUUID().replace(/-/g, "").toUpperCase().slice(0, 32);
const freshCodes = () => Array.from({ length: 10 }, () => crypto.randomUUID().slice(0, 19));

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(listMyConnectedApps).mockResolvedValue([]);
  vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: false, recoveryCodesLeft: 0 });
  vi.mocked(stepUp).mockResolvedValue({ token: crypto.randomUUID(), expiresAt: "2099-01-01T00:00:00Z" });
  const key = freshKey();
  vi.mocked(startTwoFactor).mockResolvedValue({ key, otpauthUri: `otpauth://totp/Cluckwork:${EMAIL}?secret=${key}` });
});

async function toScan() {
  await screen.findByRole("heading", { name: t("twoFactorHeading") }, { timeout: 3000 }).catch((e: unknown) => { screen.debug(undefined, 4000); throw e; });
  await act(async () => { fireEvent.click(screen.getByRole("button", { name: t("setUpButton") })); });
  fireEvent.change(within(screen.getByRole("dialog")).getByLabelText(/Your current password/), { target: { value: crypto.randomUUID() } });
  await act(async () => { fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: t("continueButton") })); });
  await screen.findByRole("img", { name: t("qrLabel") });
  fireEvent.change(screen.getByLabelText(new RegExp(t("codeLabel"))), { target: { value: freshCode() } });
}

describe("while confirm is in flight", () => {
  it("makes the rail and the bottom nav inert, and frees them when it settles", async () => {
    let answer!: (value: { recoveryCodes?: string[] }) => void;
    vi.mocked(confirmTwoFactor).mockReturnValue(new Promise((resolve) => { answer = resolve; }));
    renderWithProviders(
      <Routes><Route element={<AppLayout />}><Route path="account" element={<AccountPage />} /></Route></Routes>,
      { route: "/account", token: { sub: "u1", role: "Worker" } },
    );
    await toScan();
    const rail = () => screen.getByRole("navigation", { name: "Primary", hidden: true });
    const tabs = () => screen.getByRole("navigation", { name: "Sections", hidden: true });
    expect(rail().closest("[inert]")).toBeNull();

    await act(async () => { fireEvent.click(screen.getByRole("button", { name: t("confirmButton") })); });

    expect(rail().closest("[inert]")).not.toBeNull();
    expect(tabs().closest("[inert]")).not.toBeNull();
    await act(async () => { answer({ recoveryCodes: undefined }); });
    await waitFor(() => expect(rail().closest("[inert]")).toBeNull());
    expect(tabs().closest("[inert]")).toBeNull();
  });
});

describe("Back with recovery codes not saved yet", () => {
  // A real BrowserRouter over jsdom's history: Back to "/" would unmount the
  // Account page, and the codes with it.
  function renderAtAccount() {
    window.history.replaceState(null, "", "/");
    window.history.pushState(null, "", "/account");
    setStoredToken({ sub: "u1", role: "Worker" });
    return render(
      <BrowserRouter>
        <AuthProvider>
          <MeContext.Provider value={{ id: "u1", email: EMAIL, name: null, role: "Worker", language: null, preferredStepperUnit: null }}>
            <FarmContext.Provider value={farmState({ farm: null })}>
              <Routes>
                <Route path="/" element={<p>Elsewhere</p>} />
                <Route path="/account" element={<AccountPage />} />
              </Routes>
            </FarmContext.Provider>
          </MeContext.Provider>
        </AuthProvider>
      </BrowserRouter>,
    );
  }

  it("keeps the codes and their dialog through repeated Back, and lets Back go once they are saved", async () => {
    const codes = freshCodes();
    vi.mocked(confirmTwoFactor).mockResolvedValue({ recoveryCodes: codes });
    renderAtAccount();
    await toScan();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: t("confirmButton") })); });
    await screen.findByRole("dialog", { name: t("codesHeading") });

    for (const _ of [1, 2]) await act(async () => { window.history.back(); await new Promise((r) => setTimeout(r, 50)); });

    expect(window.location.pathname).toBe("/account");
    expect(screen.getByRole("dialog", { name: t("codesHeading") })).toBeInTheDocument();
    expect(screen.getByText(codes[0]!)).toBeInTheDocument();

    fireEvent.click(screen.getByRole("checkbox", { name: t("savedCheckbox") }));
    await act(async () => { await new Promise((r) => setTimeout(r, 50)); });
    await act(async () => { window.history.back(); await new Promise((r) => setTimeout(r, 50)); });

    await waitFor(() => expect(screen.getByText("Elsewhere")).toBeInTheDocument());
  });

  it("asks before the tab closes until the codes are saved, and not after", async () => {
    vi.mocked(confirmTwoFactor).mockResolvedValue({ recoveryCodes: freshCodes() });
    renderAtAccount();
    await toScan();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: t("confirmButton") })); });
    await screen.findByRole("dialog", { name: t("codesHeading") });
    const closing = () => {
      const event = new Event("beforeunload", { cancelable: true });
      window.dispatchEvent(event);
      return event.defaultPrevented;
    };

    expect(closing()).toBe(true);
    fireEvent.click(screen.getByRole("checkbox", { name: t("savedCheckbox") }));
    expect(closing()).toBe(false);
  });
});
