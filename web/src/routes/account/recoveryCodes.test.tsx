import { useState } from "react";
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { BrowserRouter, Route, Routes, useLocation } from "react-router";
import { AccountPage } from "../AccountPage";
import { AppLayout } from "../AppLayout";
import { ProtectedRoute } from "../ProtectedRoute";
import { RecoveryCodesProvider } from "./recoveryCodes";
import { farmState } from "../../test/fixtures";
import { freshCode } from "../../test/codes";
import { setStoredToken } from "../../test/jwt";
import { AuthProvider } from "../../auth/AuthContext";
import { FarmContext } from "../../farm/FarmContext";
import { MeContext } from "../../session/SessionContext";
import { getTwoFactorStatus, listMyConnectedApps, regenerateRecoveryCodes, startTwoFactor } from "../../api/cluckwork";
import { ApiError, confirmTwoFactor, setOnUnauthenticated, stepUp } from "../../api/client";
import { logout as endSessionHere } from "../../api/session";
import i18n from "../../i18n";

// #1277 MC4 (maintainer, 2026-11-05: "Keep codes above pages"): unsaved
// recovery codes, and the answer of a confirm or regenerate still in flight,
// live in a slot above the routes. No navigation drops them; only the end of
// the session does. Driven through a real BrowserRouter over jsdom's history,
// because a MemoryRouter would ignore Back and the skip link.
vi.mock("../../api/cluckwork", async (importOriginal) => ({
  ...await importOriginal<typeof import("../../api/cluckwork")>(),
  getTwoFactorStatus: vi.fn(),
  startTwoFactor: vi.fn(),
  regenerateRecoveryCodes: vi.fn(),
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

const t = (key: string, options: Record<string, unknown> = {}): string => i18n.t(`account:${key}` as "account:heading", options);
const EMAIL = "test@farm.local";
const freshKey = () => crypto.randomUUID().replace(/-/g, "").toUpperCase().slice(0, 32);
const freshCodes = () => Array.from({ length: 10 }, () => crypto.randomUUID().slice(0, 19));
const codesDialog = () => screen.queryByRole("dialog", { name: t("codesHeading") });

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (err: unknown) => void;
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

// The form route shows its location key and keeps a typed value in state.
function FormScreen() {
  const [typed, setTyped] = useState("");
  return (
    <>
      <p data-testid="location-key">{useLocation().key}</p>
      <input aria-label="Notes" value={typed} onChange={(e) => setTyped(e.target.value)} />
    </>
  );
}

// History before the test: /help, then /, then the starting path, so Back
// lands on "/" and history.go(-2) on "/help".
function renderApp(path: string, enrolled: boolean) {
  window.history.replaceState(null, "", "/help");
  window.history.pushState(null, "", "/");
  window.history.pushState(null, "", path);
  setStoredToken({ sub: "u1", role: "Worker", ...(enrolled && { two_factor: "true" }) });
  return render(
    <AuthProvider>
      <RecoveryCodesProvider>
        <BrowserRouter>
          <MeContext.Provider value={{ id: "u1", email: EMAIL, name: null, role: "Worker", language: null, preferredStepperUnit: null }}>
            <FarmContext.Provider value={farmState({ farm: null })}>
              <Routes>
                <Route path="/login" element={<p>Login page</p>} />
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
    </AuthProvider>,
  );
}

const rail = () => screen.getByRole("navigation", { name: i18n.t("nav:primaryNavAriaLabel"), hidden: true });
const skipLink = () => screen.getByRole("link", { name: i18n.t("nav:skipToContent"), hidden: true });

// Each way to leave the Account page, and the route it leaves for.
const LEAVE = {
  "a nav click": { go: () => fireEvent.click(rail().querySelector('a[href="/"]')!), lands: "Elsewhere" },
  "a single Back": { go: () => window.history.back(), lands: "Elsewhere" },
  "history.go(-2)": { go: () => window.history.go(-2), lands: "Help page" },
  "the skip link": { go: () => fireEvent.click(skipLink()), lands: null },
} as const;

async function proveAndSubmit(submit: string, enrolled: boolean) {
  const dialog = await screen.findByRole("dialog");
  fireEvent.change(within(dialog).getByLabelText(/Your current password/), { target: { value: crypto.randomUUID() } });
  if (enrolled) fireEvent.change(within(dialog).getByLabelText(/Authenticator or recovery code/), { target: { value: freshCode() } });
  await act(async () => { fireEvent.click(within(dialog).getByRole("button", { name: submit })); });
}

async function confirmPending() {
  const answer = deferred<{ recoveryCodes?: string[] }>();
  vi.mocked(confirmTwoFactor).mockReturnValue(answer.promise);
  renderApp("/account", false);
  const setUp = await screen.findByRole("button", { name: t("setUpButton") }, { timeout: 3000 });
  await act(async () => { fireEvent.click(setUp); });
  await proveAndSubmit(t("continueButton"), false);
  await screen.findByRole("img", { name: t("qrLabel") });
  fireEvent.change(screen.getByLabelText(new RegExp(t("codeLabel"))), { target: { value: freshCode() } });
  await act(async () => { fireEvent.click(screen.getByRole("button", { name: t("confirmButton") })); });
  expect(confirmTwoFactor).toHaveBeenCalledTimes(1);
  return answer;
}

async function regeneratePending() {
  const answer = deferred<{ recoveryCodes: string[] }>();
  vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: true, recoveryCodesLeft: 1 });
  vi.mocked(regenerateRecoveryCodes).mockReturnValue(answer.promise);
  renderApp("/account", true);
  const regenerate = await screen.findByRole("button", { name: t("regenerateButton") }, { timeout: 3000 });
  await act(async () => { fireEvent.click(regenerate); });
  await proveAndSubmit(t("regenerateButton"), true);
  expect(regenerateRecoveryCodes).toHaveBeenCalledTimes(1);
  return answer;
}

async function leave(how: keyof typeof LEAVE) {
  const { go, lands } = LEAVE[how];
  await act(async () => { go(); });
  if (lands) await screen.findByText(lands);
}

async function codesShown(codes: string[]) {
  const dialog = await screen.findByRole("dialog", { name: t("codesHeading") });
  expect(within(dialog).getAllByRole("listitem").map((li) => li.textContent)).toEqual(codes);
  return dialog;
}

// The session's last request reported it over, as endSession does.
const sessionEnds = async (title: string) => {
  const onUnauthenticated = vi.mocked(setOnUnauthenticated).mock.calls.at(-1)?.[0];
  if (!onUnauthenticated) throw new Error("AuthProvider registered no session-end handler");
  await act(async () => { onUnauthenticated(title); });
};

// Every place the page could still hold a code: the DOM and web storage. The
// floor is the login page itself, so the walk read a live document.
function expectNoCodesAnywhere(codes: string[]) {
  const readable = [document.body.innerHTML, JSON.stringify({ ...localStorage }), JSON.stringify({ ...sessionStorage })].join("\n");
  expect(readable).toContain("Login page");
  for (const code of codes) expect(readable).not.toContain(code);
}

beforeEach(() => {
  vi.resetAllMocks();
  vi.stubGlobal("fetch", vi.fn(async () => new Response(null, { status: 204 })));
  vi.mocked(listMyConnectedApps).mockResolvedValue([]);
  vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: false, recoveryCodesLeft: 0 });
  vi.mocked(stepUp).mockImplementation(async () => ({ token: crypto.randomUUID(), expiresAt: "2099-01-01T00:00:00Z" }));
  const key = freshKey();
  vi.mocked(startTwoFactor).mockResolvedValue({ key, otpauthUri: `otpauth://totp/Cluckwork:${EMAIL}?secret=${key}` });
});

afterEach(() => vi.unstubAllGlobals());

describe("1. while confirm is in flight", () => {
  it.each(Object.keys(LEAVE) as (keyof typeof LEAVE)[])("%s leaves, and the codes still show when confirm settles", async (how) => {
    const answer = await confirmPending();
    const codes = freshCodes();

    await leave(how);
    await act(async () => { answer.resolve({ recoveryCodes: codes }); });

    await codesShown(codes);
  });
});

describe("2. while the codes are unsaved", () => {
  const STAY = {
    "a nav click": async () => { await leave("a nav click"); },
    "Back": async () => { await leave("a single Back"); },
    "history.go(-2)": async () => { await leave("history.go(-2)"); },
    "Forward": async () => {
      await leave("a single Back");
      await act(async () => { window.history.forward(); });
      await waitFor(() => expect(window.location.pathname).toBe("/account"));
    },
  } as const;

  it.each(Object.keys(STAY) as (keyof typeof STAY)[])("%s keeps the dialog and the codes", async (how) => {
    const answer = await confirmPending();
    const codes = freshCodes();
    await act(async () => { answer.resolve({ recoveryCodes: codes }); });
    await codesShown(codes);

    await STAY[how]();

    await codesShown(codes);
  });
});

describe("3. while regenerate is in flight", () => {
  it.each(Object.keys(LEAVE) as (keyof typeof LEAVE)[])("%s leaves, and the new codes still show when it settles", async (how) => {
    const answer = await regeneratePending();
    const codes = freshCodes();

    await leave(how);
    await act(async () => { answer.resolve({ recoveryCodes: codes }); });

    const dialog = await codesShown(codes);
    expect(within(dialog).getByText(t("codesReplaced"))).toBeInTheDocument();
  });
});

describe("4. when the session ends", () => {
  it("while confirm is in flight, because the resend's fresh token was refused, no codes appear", async () => {
    const answer = await confirmPending();
    const codes = freshCodes();

    answer.reject(new ApiError(401, "Auth.CredentialsSuperseded", "Your credentials changed."));
    await sessionEnds("Auth.CredentialsSuperseded");

    await screen.findByText("Login page");
    expect(codesDialog()).toBeNull();
    expectNoCodesAnywhere(codes);
  });

  it("while confirm is in flight, by a sign-out, the late answer's codes are dropped", async () => {
    const answer = await confirmPending();
    const codes = freshCodes();

    await act(async () => { await endSessionHere(); });
    await act(async () => { answer.resolve({ recoveryCodes: codes }); });
    expect(codesDialog()).toBeNull();
    await sessionEnds("Auth.SessionEnded");

    await screen.findByText("Login page");
    expectNoCodesAnywhere(codes);
  });

  it("with codes on screen and unsaved, the codes go with the session", async () => {
    const answer = await confirmPending();
    const codes = freshCodes();
    await act(async () => { answer.resolve({ recoveryCodes: codes }); });
    await codesShown(codes);

    await sessionEnds("Auth.CredentialsSuperseded");

    await screen.findByText("Login page");
    expect(codesDialog()).toBeNull();
    expectNoCodesAnywhere(codes);
  });
});

describe("5. a second set of codes in one mount", () => {
  it("starts unticked, with Done disabled", async () => {
    const answer = await confirmPending();
    vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: true, recoveryCodesLeft: 10 });
    await act(async () => { answer.resolve({ recoveryCodes: freshCodes() }); });
    const first = await screen.findByRole("dialog", { name: t("codesHeading") });
    fireEvent.click(within(first).getByRole("checkbox", { name: t("savedCheckbox") }));
    await act(async () => { fireEvent.click(within(first).getByRole("button", { name: t("doneButton") })); });
    await waitFor(() => expect(codesDialog()).toBeNull());

    const codes = freshCodes();
    vi.mocked(regenerateRecoveryCodes).mockResolvedValue({ recoveryCodes: codes });
    const regenerate = await screen.findByRole("button", { name: t("regenerateButton") });
    await act(async () => { fireEvent.click(regenerate); });
    await proveAndSubmit(t("regenerateButton"), false);

    const second = await codesShown(codes);
    expect(within(second).getByRole("checkbox", { name: t("savedCheckbox") })).not.toBeChecked();
    expect(within(second).getByRole("button", { name: t("doneButton") })).toBeDisabled();
  });
});

describe("6. closing the tab", () => {
  const closing = () => {
    const event = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(event);
    return event.defaultPrevented;
  };

  it("asks first while the codes are unsaved, and not once they are saved or done", async () => {
    const answer = await confirmPending();
    await act(async () => { answer.resolve({ recoveryCodes: freshCodes() }); });
    const dialog = await screen.findByRole("dialog", { name: t("codesHeading") });

    expect(closing()).toBe(true);
    fireEvent.click(within(dialog).getByRole("checkbox", { name: t("savedCheckbox") }));
    expect(closing()).toBe(false);
    await act(async () => { fireEvent.click(within(dialog).getByRole("button", { name: t("doneButton") })); });
    expect(closing()).toBe(false);
  });
});

describe("7. focus after Done", () => {
  async function done() {
    const dialog = await screen.findByRole("dialog", { name: t("codesHeading") });
    fireEvent.click(within(dialog).getByRole("checkbox", { name: t("savedCheckbox") }));
    await act(async () => { fireEvent.click(within(dialog).getByRole("button", { name: t("doneButton") })); });
  }

  it("goes to the Security section when Account is on screen", async () => {
    const answer = await confirmPending();
    await act(async () => { answer.resolve({ recoveryCodes: freshCodes() }); });

    await done();

    await waitFor(() => expect(document.activeElement?.id).toBe("account-security"));
  });

  it("goes to the main content when another page is", async () => {
    const answer = await confirmPending();
    await leave("a nav click");
    await act(async () => { answer.resolve({ recoveryCodes: freshCodes() }); });

    await done();

    await waitFor(() => expect(document.activeElement?.id).toBe("main-content"));
  });
});
