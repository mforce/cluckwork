// #1277/#1398 — the app tree, mocks set-up and helpers the recovery-code
// tests share. The test files mock api/cluckwork and api/client themselves;
// this module only reads the mocks.
import { useState } from "react";
import { expect, vi } from "vitest";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { BrowserRouter, Route, Routes, useLocation } from "react-router";
import { AccountPage } from "../routes/AccountPage";
import { AppLayout } from "../routes/AppLayout";
import { ProtectedRoute } from "../routes/ProtectedRoute";
import { RecoveryCodesProvider } from "../routes/account/recoveryCodes";
import { AppNoticeProvider } from "../components/AppNotice";
import { farmState } from "./fixtures";
import { freshCode } from "./codes";
import { makeToken, setStoredToken } from "./jwt";
import { AuthProvider } from "../auth/AuthContext";
import { useAuth } from "../auth/useAuth";
import { FarmContext } from "../farm/FarmContext";
import { MeContext } from "../session/SessionContext";
import { getTwoFactorStatus, listMyConnectedApps, regenerateRecoveryCodes, startTwoFactor } from "../api/cluckwork";
import { confirmTwoFactor, setOnUnauthenticated, stepUp } from "../api/client";
import i18n from "../i18n";


export const t = (key: string, options: Record<string, unknown> = {}): string => i18n.t(`account:${key}` as "account:heading", options);
export const EMAIL = "test@farm.local";
export const freshKey = () => crypto.randomUUID().replace(/-/g, "").toUpperCase().slice(0, 32);
export const freshCodes = () => Array.from({ length: 10 }, () => crypto.randomUUID().slice(0, 19));
export const codesDialog = () => screen.queryByRole("dialog", { name: t("codesHeading") });

export function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (err: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

// The form route shows its location key and keeps a typed value in state.
export function FormScreen() {
  const [typed, setTyped] = useState("");
  return (
    <>
      <p data-testid="location-key">{useLocation().key}</p>
      <input aria-label="Notes" value={typed} onChange={(e) => setTyped(e.target.value)} />
    </>
  );
}

// The login route, with a way to sign in again through the real AuthProvider.
export function LoginPage() {
  const { login } = useAuth();
  return (
    <>
      <p>Login page</p>
      <button type="button" onClick={() => void login("farm", EMAIL, crypto.randomUUID())}>Sign in again</button>
    </>
  );
}

// History before the test: /help, then /, then the starting path, so Back
// lands on "/" and history.go(-2) on "/help".
export function renderApp(path: string, enrolled: boolean) {
  window.history.replaceState(null, "", "/help");
  window.history.pushState(null, "", "/");
  window.history.pushState(null, "", path);
  setStoredToken({ sub: "u1", role: "Worker", ...(enrolled && { two_factor: "true" }) });
  return render(
    <AuthProvider>
      <AppNoticeProvider>
      <RecoveryCodesProvider>
        <BrowserRouter>
          <MeContext.Provider value={{ id: "u1", email: EMAIL, name: null, role: "Worker", language: null, preferredStepperUnit: null }}>
            <FarmContext.Provider value={farmState({ farm: null })}>
              <Routes>
                <Route path="/login" element={<LoginPage />} />
                <Route element={<ProtectedRoute />}>
                  <Route element={<AppLayout />}>
                    <Route index element={<p>Elsewhere</p>} />
                    <Route path="help" element={<p>Help page</p>} />
                    <Route path="account" element={<AccountPage />} />
                    <Route path="form" element={<FormScreen />} />
                  </Route>
                </Route>
              </Routes>
            </FarmContext.Provider>
          </MeContext.Provider>
        </BrowserRouter>
      </RecoveryCodesProvider>
      </AppNoticeProvider>
    </AuthProvider>,
  );
}

export const rail = () => screen.getByRole("navigation", { name: i18n.t("nav:primaryNavAriaLabel"), hidden: true });
export const skipLink = () => screen.getByRole("link", { name: i18n.t("nav:skipToContent"), hidden: true });

// Each way to leave the Account page, and the route it leaves for. The skip
// link stays on the page now, so its own test is at the end.
export const LEAVE = {
  "a nav click": { go: () => fireEvent.click(rail().querySelector('a[href="/"]')!), lands: "Elsewhere" },
  "a single Back": { go: () => window.history.back(), lands: "Elsewhere" },
  "history.go(-2)": { go: () => window.history.go(-2), lands: "Help page" },
} as const;

export async function proveAndSubmit(submit: string, enrolled: boolean) {
  const dialog = await screen.findByRole("dialog");
  fireEvent.change(within(dialog).getByLabelText(/Your current password/), { target: { value: crypto.randomUUID() } });
  if (enrolled) fireEvent.change(within(dialog).getByLabelText(/Authenticator or recovery code/), { target: { value: freshCode() } });
  await act(async () => { fireEvent.click(within(dialog).getByRole("button", { name: submit })); });
}

// On the set-up step, with a code typed and Confirm code not yet pressed.
export async function toScan() {
  renderApp("/account", false);
  const setUp = await screen.findByRole("button", { name: t("setUpButton") }, { timeout: 3000 });
  await act(async () => { fireEvent.click(setUp); });
  await proveAndSubmit(t("continueButton"), false);
  await screen.findByRole("img", { name: t("qrLabel") });
  fireEvent.change(screen.getByLabelText(new RegExp(t("codeLabel"))), { target: { value: freshCode() } });
}

export async function confirmPending() {
  const answer = deferred<{ recoveryCodes?: string[] }>();
  vi.mocked(confirmTwoFactor).mockReturnValue(answer.promise);
  await toScan();
  await act(async () => { fireEvent.click(screen.getByRole("button", { name: t("confirmButton") })); });
  expect(confirmTwoFactor).toHaveBeenCalledTimes(1);
  return answer;
}

// A regenerate whose step-up, or whose answer, the test settles by hand.
export async function regenerateOpen() {
  vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: true, recoveryCodesLeft: 1 });
  renderApp("/account", true);
  const regenerate = await screen.findByRole("button", { name: t("regenerateButton") }, { timeout: 3000 });
  await act(async () => { fireEvent.click(regenerate); });
}

export async function regeneratePending() {
  const answer = deferred<{ recoveryCodes: string[] }>();
  vi.mocked(regenerateRecoveryCodes).mockReturnValue(answer.promise);
  await regenerateOpen();
  await proveAndSubmit(t("regenerateButton"), true);
  expect(regenerateRecoveryCodes).toHaveBeenCalledTimes(1);
  return answer;
}

export async function leave(how: keyof typeof LEAVE) {
  const { go, lands } = LEAVE[how];
  await act(async () => { go(); });
  await screen.findByText(lands);
}

export const backToAccount = async () => {
  await act(async () => { fireEvent.click(rail().querySelector('a[href="/account"]')!); });
  await screen.findByRole("heading", { name: t("twoFactorHeading") }, { timeout: 3000 });
};

export async function codesShown(codes: string[]) {
  const dialog = await screen.findByRole("dialog", { name: t("codesHeading") });
  expect(within(dialog).getAllByRole("listitem").map((li) => li.textContent)).toEqual(codes);
  return dialog;
}

// The session's last request reported it over, as endSession does.
export const sessionEnds = async (title: string) => {
  const onUnauthenticated = vi.mocked(setOnUnauthenticated).mock.calls.at(-1)?.[0];
  if (!onUnauthenticated) throw new Error("AuthProvider registered no session-end handler");
  await act(async () => { onUnauthenticated(title); });
};

// Every place the page could still hold a code: the DOM, web storage, the URL
// and the history entry. The floor is the login page itself, so the walk read
// a live document.
export function expectNoCodesAnywhere(codes: string[]) {
  const readable = [
    document.body.innerHTML, JSON.stringify({ ...localStorage }), JSON.stringify({ ...sessionStorage }),
    window.location.href, JSON.stringify(window.history.state),
  ].join("\n");
  expect(readable).toContain("Login page");
  for (const code of codes) expect(readable).not.toContain(code);
}

// Focus moves on the next frame (done in recoveryCodes.tsx), so a check of
// where it rests waits a few frames.
export const frames = (n: number) => act(async () => {
  for (let i = 0; i < n; i++) await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
});

// #1398 — the shell strip's notice after a drop, and its offscreen region.
export const droppedNotice = () => document.querySelector<HTMLElement>(".shell-notice");
export const politeRegion = () => document.querySelector<HTMLElement>(".sr-only[aria-live='polite']");

export const tabCloseAsks = () => {
  const event = new Event("beforeunload", { cancelable: true });
  window.dispatchEvent(event);
  return event.defaultPrevented;
};

export function setUpRecoveryCodesApp() {
  vi.resetAllMocks();
  // Only sign-in answers with a body: a fresh token for the same farm.
  vi.stubGlobal("fetch", vi.fn(async (url: string) => (url.endsWith("/auth/login")
    ? new Response(JSON.stringify({ accessToken: makeToken({ sub: "u1", role: "Worker", account_id: "acct-1" }), accessTokenExpiry: "2099-01-01T00:00:00Z" }), { status: 200, headers: { "Content-Type": "application/json" } })
    : new Response(null, { status: 204 }))));
  vi.mocked(listMyConnectedApps).mockResolvedValue([]);
  vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: false, recoveryCodesLeft: 0 });
  vi.mocked(stepUp).mockImplementation(async () => ({ token: crypto.randomUUID(), expiresAt: "2099-01-01T00:00:00Z" }));
  const key = freshKey();
  vi.mocked(startTwoFactor).mockResolvedValue({ key, otpauthUri: `otpauth://totp/Cluckwork:${EMAIL}?secret=${key}` });
}
