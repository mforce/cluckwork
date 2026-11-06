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
import { makeToken, setStoredToken } from "../../test/jwt";
import { getAccessToken } from "../../auth/tokenStore";
import { AuthProvider } from "../../auth/AuthContext";
import { useAuth } from "../../auth/useAuth";
import { FarmContext } from "../../farm/FarmContext";
import { MeContext } from "../../session/SessionContext";
import { getTwoFactorStatus, listMyConnectedApps, regenerateRecoveryCodes, startTwoFactor } from "../../api/cluckwork";
import type { TwoFactorStatus } from "../../api/cluckwork";
import { ApiError, confirmTwoFactor, logout, setOnTokensChanged, setOnUnauthenticated, stepUp } from "../../api/client";
import { isSignedOut, login as signInHere, logout as endSessionHere } from "../../api/session";
import i18n from "../../i18n";

// #1277 (maintainer, 2026-11-05: "Keep codes above pages"): unsaved
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

// The login route, with a way to sign in again through the real AuthProvider.
function LoginPage() {
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
    </AuthProvider>,
  );
}

const rail = () => screen.getByRole("navigation", { name: i18n.t("nav:primaryNavAriaLabel"), hidden: true });
const skipLink = () => screen.getByRole("link", { name: i18n.t("nav:skipToContent"), hidden: true });

// Each way to leave the Account page, and the route it leaves for. The skip
// link stays on the page now, so its own test is at the end.
const LEAVE = {
  "a nav click": { go: () => fireEvent.click(rail().querySelector('a[href="/"]')!), lands: "Elsewhere" },
  "a single Back": { go: () => window.history.back(), lands: "Elsewhere" },
  "history.go(-2)": { go: () => window.history.go(-2), lands: "Help page" },
} as const;

async function proveAndSubmit(submit: string, enrolled: boolean) {
  const dialog = await screen.findByRole("dialog");
  fireEvent.change(within(dialog).getByLabelText(/Your current password/), { target: { value: crypto.randomUUID() } });
  if (enrolled) fireEvent.change(within(dialog).getByLabelText(/Authenticator or recovery code/), { target: { value: freshCode() } });
  await act(async () => { fireEvent.click(within(dialog).getByRole("button", { name: submit })); });
}

// On the set-up step, with a code typed and Confirm code not yet pressed.
async function toScan() {
  renderApp("/account", false);
  const setUp = await screen.findByRole("button", { name: t("setUpButton") }, { timeout: 3000 });
  await act(async () => { fireEvent.click(setUp); });
  await proveAndSubmit(t("continueButton"), false);
  await screen.findByRole("img", { name: t("qrLabel") });
  fireEvent.change(screen.getByLabelText(new RegExp(t("codeLabel"))), { target: { value: freshCode() } });
}

async function confirmPending() {
  const answer = deferred<{ recoveryCodes?: string[] }>();
  vi.mocked(confirmTwoFactor).mockReturnValue(answer.promise);
  await toScan();
  await act(async () => { fireEvent.click(screen.getByRole("button", { name: t("confirmButton") })); });
  expect(confirmTwoFactor).toHaveBeenCalledTimes(1);
  return answer;
}

// A regenerate whose step-up, or whose answer, the test settles by hand.
async function regenerateOpen() {
  vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: true, recoveryCodesLeft: 1 });
  renderApp("/account", true);
  const regenerate = await screen.findByRole("button", { name: t("regenerateButton") }, { timeout: 3000 });
  await act(async () => { fireEvent.click(regenerate); });
}

async function regeneratePending() {
  const answer = deferred<{ recoveryCodes: string[] }>();
  vi.mocked(regenerateRecoveryCodes).mockReturnValue(answer.promise);
  await regenerateOpen();
  await proveAndSubmit(t("regenerateButton"), true);
  expect(regenerateRecoveryCodes).toHaveBeenCalledTimes(1);
  return answer;
}

async function leave(how: keyof typeof LEAVE) {
  const { go, lands } = LEAVE[how];
  await act(async () => { go(); });
  await screen.findByText(lands);
}

const backToAccount = async () => {
  await act(async () => { fireEvent.click(rail().querySelector('a[href="/account"]')!); });
  await screen.findByRole("heading", { name: t("twoFactorHeading") }, { timeout: 3000 });
};

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

// Every place the page could still hold a code: the DOM, web storage, the URL
// and the history entry. The floor is the login page itself, so the walk read
// a live document.
function expectNoCodesAnywhere(codes: string[]) {
  const readable = [
    document.body.innerHTML, JSON.stringify({ ...localStorage }), JSON.stringify({ ...sessionStorage }),
    window.location.href, JSON.stringify(window.history.state),
  ].join("\n");
  expect(readable).toContain("Login page");
  for (const code of codes) expect(readable).not.toContain(code);
}

const tabCloseAsks = () => {
  const event = new Event("beforeunload", { cancelable: true });
  window.dispatchEvent(event);
  return event.defaultPrevented;
};

beforeEach(() => {
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
});

afterEach(() => vi.unstubAllGlobals());

describe("while confirm is in flight", () => {
  it.each(Object.keys(LEAVE) as (keyof typeof LEAVE)[])("%s leaves, and the codes still show when confirm settles", async (how) => {
    const answer = await confirmPending();
    const codes = freshCodes();

    await leave(how);
    await act(async () => { answer.resolve({ recoveryCodes: codes }); });

    await codesShown(codes);
  });
});

describe("while the codes are unsaved", () => {
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

describe("while regenerate is in flight", () => {
  it.each(Object.keys(LEAVE) as (keyof typeof LEAVE)[])("%s leaves, and the new codes still show when it settles", async (how) => {
    const answer = await regeneratePending();
    const codes = freshCodes();

    await leave(how);
    await act(async () => { answer.resolve({ recoveryCodes: codes }); });

    const dialog = await codesShown(codes);
    expect(within(dialog).getByText(t("codesReplaced"))).toBeInTheDocument();
  });
});

describe("when the session ends", () => {
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

describe("a second set of codes in one mount", () => {
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

describe("closing the tab", () => {
  it("asks first while the codes are unsaved, and not once they are saved or done", async () => {
    const answer = await confirmPending();
    await act(async () => { answer.resolve({ recoveryCodes: freshCodes() }); });
    const dialog = await screen.findByRole("dialog", { name: t("codesHeading") });

    expect(tabCloseAsks()).toBe(true);
    fireEvent.click(within(dialog).getByRole("checkbox", { name: t("savedCheckbox") }));
    expect(tabCloseAsks()).toBe(false);
    await act(async () => { fireEvent.click(within(dialog).getByRole("button", { name: t("doneButton") })); });
    expect(tabCloseAsks()).toBe(false);
  });
});

describe("focus after Done", () => {
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

describe("closing the tab while a code-issuing request is in flight", () => {
  it("asks while confirm is pending, on Account and after leaving it", async () => {
    await confirmPending();
    expect(tabCloseAsks()).toBe(true);

    await leave("a nav click");

    expect(tabCloseAsks()).toBe(true);
  });

  it("asks while regenerate is pending", async () => {
    await regeneratePending();

    expect(tabCloseAsks()).toBe(true);
  });

  it("stops asking when confirm fails without codes", async () => {
    const answer = await confirmPending();

    await act(async () => { answer.reject(new ApiError(400, "Auth.EnrolmentCodeIncorrect", "Wrong.", { extensions: { attemptsLeft: 3 } })); });

    await waitFor(() => expect(tabCloseAsks()).toBe(false));
  });

  it("stops asking when the session ends, and the late answer shows nothing", async () => {
    const answer = await confirmPending();
    const codes = freshCodes();

    await sessionEnds("Auth.CredentialsSuperseded");
    expect(tabCloseAsks()).toBe(false);
    await act(async () => { answer.resolve({ recoveryCodes: codes }); });

    await screen.findByText("Login page");
    expect(tabCloseAsks()).toBe(false);
    expectNoCodesAnywhere(codes);
  });
});

describe("one code-issuing request at a time", () => {
  it("disables Regenerate and Replace while regenerate is pending, also after leaving and coming back", async () => {
    await regeneratePending();
    await leave("a nav click");

    await backToAccount();

    expect(screen.getByRole("button", { name: t("regenerateButton") })).toBeDisabled();
    expect(screen.getByRole("button", { name: t("replaceButton") })).toBeDisabled();
  });

  it("disables Turn off while regenerate is pending, after leaving and coming back", async () => {
    await regeneratePending();
    await leave("a nav click");

    await backToAccount();

    expect(screen.getByRole("button", { name: t("turnOffButton") })).toBeDisabled();
  });

  it("disables Set up while confirm is pending, after leaving and coming back", async () => {
    await confirmPending();
    await leave("a nav click");

    await backToAccount();

    expect(screen.getByRole("button", { name: t("setUpButton") })).toBeDisabled();
  });
});

describe("what the slot holds after a request ends", () => {
  it("(a) after a session end with regenerate pending, and the next sign-in, the answer shows nothing", async () => {
    const answer = await regeneratePending();
    const codes = freshCodes();
    await sessionEnds("Auth.CredentialsSuperseded");
    await screen.findByText("Login page");

    // endSession leaves the generation alone, so only the session-end clear
    // stands between this answer and the login page.
    await act(async () => { answer.resolve({ recoveryCodes: codes }); });
    expect(codesDialog()).toBeNull();
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Sign in again" })); });
    await waitFor(() => expect(getAccessToken()).not.toBeNull());

    expect(codesDialog()).toBeNull();
    expectNoCodesAnywhere(codes);
  });

  it("(b) a set whose session ended while regenerate was pending is dropped", async () => {
    const answer = await regeneratePending();

    await act(async () => { await endSessionHere(); });
    await act(async () => { answer.resolve({ recoveryCodes: freshCodes() }); });

    expect(codesDialog()).toBeNull();
  });

  it("(c) a proof that lands after its session ended sends nothing", async () => {
    const proof = deferred<{ token: string; expiresAt: string }>();
    vi.mocked(stepUp).mockReturnValueOnce(proof.promise);
    vi.mocked(regenerateRecoveryCodes).mockResolvedValue({ recoveryCodes: freshCodes() });
    await regenerateOpen();
    await proveAndSubmit(t("regenerateButton"), true);

    await act(async () => { await endSessionHere(); });
    await act(async () => { proof.resolve({ token: crypto.randomUUID(), expiresAt: "2099-01-01T00:00:00Z" }); });

    expect(regenerateRecoveryCodes).not.toHaveBeenCalled();
    expect(codesDialog()).toBeNull();
    expect(tabCloseAsks()).toBe(false);
  });

  it("(e) a proof from before a sign-out sends nothing after a newer sign-in", async () => {
    const proof = deferred<{ token: string; expiresAt: string }>();
    vi.mocked(stepUp).mockReturnValueOnce(proof.promise);
    vi.mocked(regenerateRecoveryCodes).mockResolvedValue({ recoveryCodes: freshCodes() });
    await regenerateOpen();
    await proveAndSubmit(t("regenerateButton"), true);

    expect(stepUp).toHaveBeenCalledOnce();

    // The session module's sign-out, not the app's Sign out: the dialog stays
    // current, so only the generation check stands in the way.
    await act(async () => {
      await endSessionHere();
      await signInHere({ farmCode: "farm", email: EMAIL, password: crypto.randomUUID() });
    });
    expect(isSignedOut()).toBe(false);
    await act(async () => { proof.resolve({ token: crypto.randomUUID(), expiresAt: "2099-01-01T00:00:00Z" }); });

    expect(regenerateRecoveryCodes).not.toHaveBeenCalled();
    expect(codesDialog()).toBeNull();
  });

  it("(d) Done empties the slot, which frees Regenerate again", async () => {
    const answer = await regeneratePending();
    await act(async () => { answer.resolve({ recoveryCodes: freshCodes() }); });
    const dialog = await screen.findByRole("dialog", { name: t("codesHeading") });
    const regenerate = () => screen.getByRole("button", { name: t("regenerateButton"), hidden: true });
    expect(regenerate()).toBeDisabled();

    fireEvent.click(within(dialog).getByRole("checkbox", { name: t("savedCheckbox") }));
    await act(async () => { fireEvent.click(within(dialog).getByRole("button", { name: t("doneButton") })); });

    await waitFor(() => expect(codesDialog()).toBeNull());
    expect(regenerate()).toBeEnabled();
    expect(tabCloseAsks()).toBe(false);
  });
});

describe("a regenerate answer without codes", () => {
  it("shows an error in its dialog and holds nothing", async () => {
    vi.mocked(regenerateRecoveryCodes).mockResolvedValue({});
    await regenerateOpen();

    await proveAndSubmit(t("regenerateButton"), true);

    expect(await within(screen.getByRole("dialog")).findByRole("alert")).toHaveTextContent(t("codesMissing"));
    expect(codesDialog()).toBeNull();
    expect(tabCloseAsks()).toBe(false);
  });
});

async function saveAndDone() {
  const dialog = await screen.findByRole("dialog", { name: t("codesHeading") });
  fireEvent.click(within(dialog).getByRole("checkbox", { name: t("savedCheckbox") }));
  await act(async () => { fireEvent.click(within(dialog).getByRole("button", { name: t("doneButton") })); });
}

describe("a Turn off that answers after the user came back", () => {
  it("shows two-factor off once the adopted session says so", async () => {
    // Turn off goes through the real client; its DELETE is held.
    let answer!: () => void;
    vi.mocked(fetch).mockImplementation(async (input: RequestInfo | URL, init?: RequestInit) => (
      String(input).endsWith("/auth/two-factor") && init?.method === "DELETE"
        ? new Promise<Response>((resolve) => {
          answer = () => resolve(new Response(JSON.stringify({ accessToken: makeToken({ sub: "u1", role: "Worker", account_id: "acct-1" }), accessTokenExpiry: "2099-01-01T00:00:00Z" }), { status: 200, headers: { "Content-Type": "application/json" } }));
        })
        : new Response(null, { status: 204 })));
    vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: true, recoveryCodesLeft: 10 });
    renderApp("/account", true);
    const turnOff = await screen.findByRole("button", { name: t("turnOffButton") }, { timeout: 3000 });
    await act(async () => { fireEvent.click(turnOff); });
    await proveAndSubmit(t("turnOffButton"), true);
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([, init]) => init?.method === "DELETE")).toBe(true));
    await leave("a single Back");
    await backToAccount();
    expect(screen.getByText(t("twoFactorOn"))).toBeInTheDocument();
    vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: false, recoveryCodesLeft: 0 });

    await act(async () => { answer(); });
    // The adopted pair carries no two_factor claim; the app hears of it as
    // AuthProvider does from the client.
    await act(async () => { vi.mocked(setOnTokensChanged).mock.calls.at(-1)![0]!(); });

    expect(await screen.findByText(t("twoFactorOff"))).toBeInTheDocument();
  });
});

describe("the status while it reloads after a request settles", () => {
  it("loads once at mount and once when the request settles, not when it starts or at Done", async () => {
    const answer = await regeneratePending();
    expect(getTwoFactorStatus).toHaveBeenCalledTimes(1);
    vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: true, recoveryCodesLeft: 10 });

    await act(async () => { answer.resolve({ recoveryCodes: freshCodes() }); });
    expect(getTwoFactorStatus).toHaveBeenCalledTimes(2);
    await saveAndDone();

    expect(getTwoFactorStatus).toHaveBeenCalledTimes(2);
    expect(screen.getByText(t("recoveryCodesLeft", { count: 10 }))).toBeInTheDocument();
  });

  // Each button stays disabled at Done until the settle reload lands. hidden:
  // the closing dialogs may still mark the page aria-hidden.
  it.each([
    ["Set up", "setUpButton", "confirm"],
    ["Regenerate", "regenerateButton", "regenerate"],
    ["Replace authenticator app", "replaceButton", "regenerate"],
    ["Turn off", "turnOffButton", "regenerate"],
  ] as const)("keeps %s disabled until the reloaded status arrives", async (_, key, request) => {
    const answer = request === "confirm" ? await confirmPending() : await regeneratePending();
    const status = deferred<TwoFactorStatus>();
    vi.mocked(getTwoFactorStatus).mockReturnValueOnce(status.promise);
    const button = () => screen.getByRole("button", { name: t(key), hidden: true });

    await act(async () => { answer.resolve({ recoveryCodes: freshCodes() }); });
    await saveAndDone();

    expect(button()).toBeDisabled();
    // The confirm case keeps two-factor off, so Set up is still the action.
    await act(async () => { status.resolve(request === "confirm" ? { enabled: false, recoveryCodesLeft: 0 } : { enabled: true, recoveryCodesLeft: 10 }); });
    await waitFor(() => expect(button()).toBeEnabled());
  });

  it.each([
    ["a network error", () => new TypeError("offline")],
    ["an aborted request", () => new DOMException("aborted by transport", "AbortError")],
  ] as const)("says so after %s, and Try again loads it", async (_, failure) => {
    const answer = await regeneratePending();
    const status = deferred<TwoFactorStatus>();
    vi.mocked(getTwoFactorStatus).mockReturnValueOnce(status.promise);
    await act(async () => { answer.resolve({ recoveryCodes: freshCodes() }); });
    await saveAndDone();

    await act(async () => { status.reject(failure()); });

    expect(await screen.findByText(t("statusLoadFailed"))).toBeInTheDocument();
    vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: true, recoveryCodesLeft: 10 });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: t("statusRetry"), hidden: true })); });
    await waitFor(() => expect(screen.getByRole("button", { name: t("regenerateButton"), hidden: true })).toBeEnabled());
  });
});

describe("a confirm answer with an empty set", () => {
  it.each([
    ["a first enrolment", false, "alert", "codesMissing"],
    ["a replacement", true, "status", "replacedMessage"],
  ] as const)("opens no codes dialog for %s, and says what happened", async (_, replacing, role, said) => {
    const answer = deferred<{ recoveryCodes?: string[] }>();
    vi.mocked(confirmTwoFactor).mockReturnValue(answer.promise);
    vi.mocked(getTwoFactorStatus).mockResolvedValue(replacing ? { enabled: true, recoveryCodesLeft: 10 } : { enabled: false, recoveryCodesLeft: 0 });
    renderApp("/account", replacing);
    const start = await screen.findByRole("button", { name: t(replacing ? "replaceButton" : "setUpButton") }, { timeout: 3000 });
    await act(async () => { fireEvent.click(start); });
    await proveAndSubmit(t("continueButton"), replacing);
    await screen.findByRole("img", { name: t("qrLabel") });
    fireEvent.change(screen.getByLabelText(new RegExp(t("codeLabel"))), { target: { value: freshCode() } });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: t("confirmButton") })); });

    await act(async () => { answer.resolve({ recoveryCodes: [] }); });

    expect(codesDialog()).toBeNull();
    expect((await screen.findByText(t(said))).closest(`[role="${role}"]`)).not.toBeNull();
  });
});

describe("the status after a refused regenerate", () => {
  it("reloads after a late 409, when the earlier read still said On", async () => {
    const answer = await regeneratePending();
    expect(screen.getByText(t("twoFactorOn"))).toBeInTheDocument();
    vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: false, recoveryCodesLeft: 0 });

    await act(async () => { answer.reject(new ApiError(409, "Auth.TwoFactorNotEnabled", "Not enabled.")); });

    expect(await within(screen.getByRole("dialog")).findByRole("alert")).toHaveTextContent(t("regenerateNotEnabled"));
    expect(await screen.findByText(t("twoFactorOff"))).toBeInTheDocument();
  });

  it("reloads after a late answer without codes, when the earlier count was old", async () => {
    const answer = deferred<{ recoveryCodes?: string[] }>();
    vi.mocked(regenerateRecoveryCodes).mockReturnValue(answer.promise);
    await regenerateOpen();
    await proveAndSubmit(t("regenerateButton"), true);
    expect(screen.getByText(t("recoveryCodesLeft", { count: 1 }))).toBeInTheDocument();
    vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: true, recoveryCodesLeft: 10 });

    await act(async () => { answer.resolve({}); });

    expect(await within(screen.getByRole("dialog")).findByRole("alert")).toHaveTextContent(t("codesMissing"));
    expect(await screen.findByText(t("recoveryCodesLeft", { count: 10 }))).toBeInTheDocument();
  });

  it("treats an empty set as missing", async () => {
    vi.mocked(regenerateRecoveryCodes).mockResolvedValue({ recoveryCodes: [] });
    await regenerateOpen();

    await proveAndSubmit(t("regenerateButton"), true);

    expect(await within(screen.getByRole("dialog")).findByRole("alert")).toHaveTextContent(t("codesMissing"));
    expect(codesDialog()).toBeNull();
  });
});

describe("the status after coming back to Account", () => {
  it("shows the regenerated count once a regenerate settles after the user came back", async () => {
    const answer = await regeneratePending();
    await leave("a nav click");
    await backToAccount();
    vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: true, recoveryCodesLeft: 10 });

    await act(async () => { answer.resolve({ recoveryCodes: freshCodes() }); });
    await saveAndDone();

    expect(await screen.findByText(t("recoveryCodesLeft", { count: 10 }))).toBeInTheDocument();
  });

  it("shows two-factor on once an enrolment settles after the user came back", async () => {
    const answer = await confirmPending();
    await leave("a nav click");
    await backToAccount();
    vi.mocked(getTwoFactorStatus).mockResolvedValue({ enabled: true, recoveryCodesLeft: 10 });

    await act(async () => { answer.resolve({ recoveryCodes: freshCodes() }); });
    await saveAndDone();

    expect(await screen.findByText(t("twoFactorOn"))).toBeInTheDocument();
  });
});

describe("regenerate's request ends on every path", () => {
  it("a refused proof ends it, and a retry goes out", async () => {
    vi.mocked(stepUp).mockRejectedValueOnce(new ApiError(400, "Users.CurrentPasswordIncorrect", "Wrong."));
    await regenerateOpen();

    await proveAndSubmit(t("regenerateButton"), true);
    expect(await within(screen.getByRole("dialog")).findByRole("alert")).toBeInTheDocument();
    expect(tabCloseAsks()).toBe(false);
    vi.mocked(regenerateRecoveryCodes).mockResolvedValueOnce({ recoveryCodes: freshCodes() });
    await proveAndSubmit(t("regenerateButton"), true);

    expect(regenerateRecoveryCodes).toHaveBeenCalledTimes(1);
    expect(codesDialog()).not.toBeNull();
  });

  it("a proof abandoned by Back ends it, and no write is sent", async () => {
    const proof = deferred<{ token: string; expiresAt: string }>();
    vi.mocked(stepUp).mockReturnValueOnce(proof.promise);
    await regenerateOpen();
    await proveAndSubmit(t("regenerateButton"), true);
    expect(tabCloseAsks()).toBe(true);

    // The proof lands in the first microtask after Back puts the next page in
    // the DOM: before a deferred passive cleanup of the page that asked, when
    // React defers it to a later task.
    const landed = new Promise<void>((resolve) => {
      const arrival = new MutationObserver(() => {
        if (!document.body.textContent?.includes("Elsewhere")) return;
        arrival.disconnect();
        proof.resolve({ token: crypto.randomUUID(), expiresAt: "2099-01-01T00:00:00Z" });
        resolve();
      });
      arrival.observe(document.body, { childList: true, subtree: true, characterData: true });
    });
    await leave("a single Back");
    await act(async () => { await landed; });

    expect(regenerateRecoveryCodes).not.toHaveBeenCalled();
    expect(tabCloseAsks()).toBe(false);
    await backToAccount();
    expect(screen.getByRole("button", { name: t("regenerateButton") })).toBeEnabled();
  });

  it("a refusal after Account is gone ends it", async () => {
    const answer = await regeneratePending();
    await leave("a single Back");

    await act(async () => { answer.reject(new ApiError(409, "Auth.TwoFactorNotEnabled", "Not enabled.")); });

    expect(tabCloseAsks()).toBe(false);
    await backToAccount();
    expect(screen.getByRole("button", { name: t("regenerateButton") })).toBeEnabled();
  });

  it("a delivery after Account is gone ends it once the codes are Done", async () => {
    const answer = await regeneratePending();
    await leave("a single Back");

    await act(async () => { answer.resolve({ recoveryCodes: freshCodes() }); });
    const dialog = screen.getByRole("dialog", { name: t("codesHeading") });
    fireEvent.click(within(dialog).getByRole("checkbox", { name: t("savedCheckbox") }));
    await act(async () => { fireEvent.click(within(dialog).getByRole("button", { name: t("doneButton") })); });

    expect(tabCloseAsks()).toBe(false);
    await backToAccount();
    expect(screen.getByRole("button", { name: t("regenerateButton") })).toBeEnabled();
  });
});

describe("a request from an ended session", () => {
  it("cannot end the next session's request, which still shows its codes", async () => {
    const first = await regeneratePending();
    await sessionEnds("Auth.SessionEnded");
    await screen.findByText("Login page");
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: "Sign in again" })); });
    await waitFor(() => expect(getAccessToken()).not.toBeNull());
    await act(async () => {
      window.history.pushState({ key: crypto.randomUUID() }, "", "/account");
      window.dispatchEvent(new PopStateEvent("popstate", { state: window.history.state }));
    });
    const regenerate = await screen.findByRole("button", { name: t("regenerateButton") }, { timeout: 3000 });
    const second = deferred<{ recoveryCodes: string[] }>();
    vi.mocked(regenerateRecoveryCodes).mockReturnValueOnce(second.promise);
    await act(async () => { fireEvent.click(regenerate); });
    await proveAndSubmit(t("regenerateButton"), false);
    expect(regenerateRecoveryCodes).toHaveBeenCalledTimes(2);

    await act(async () => { first.resolve({ recoveryCodes: freshCodes() }); });

    expect(codesDialog()).toBeNull();
    expect(tabCloseAsks()).toBe(true);
    const codes = freshCodes();
    await act(async () => { second.resolve({ recoveryCodes: codes }); });
    await codesShown(codes);
  });
});

describe("a first enrolment's answer without codes", () => {
  it("says so on the set-up step and holds nothing", async () => {
    const answer = await confirmPending();

    await act(async () => { answer.resolve({}); });

    expect(await screen.findByRole("alert")).toHaveTextContent(t("codesMissing"));
    expect(codesDialog()).toBeNull();
    expect(tabCloseAsks()).toBe(false);
  });
});

describe("Sign out while the set-up step is open", () => {
  it("sends no confirm once Sign out has been pressed, and no codes appear", async () => {
    // The app's Sign out runs the real session sign-out, its request held.
    let finish!: () => void;
    vi.mocked(fetch).mockImplementation(async (input: RequestInfo | URL) => (String(input).endsWith("/auth/logout")
      ? new Promise<Response>((resolve) => { finish = () => resolve(new Response(null, { status: 204 })); })
      : new Response(null, { status: 204 })));
    vi.mocked(logout).mockImplementation(() => endSessionHere());
    vi.mocked(confirmTwoFactor).mockResolvedValue({ recoveryCodes: freshCodes() });
    await toScan();

    await act(async () => { fireEvent.click(within(rail().closest("aside")!).getByRole("button", { name: i18n.t("nav:signOut"), hidden: true })); });
    await act(async () => { fireEvent.click(screen.getByRole("button", { name: t("confirmButton") })); });

    expect(confirmTwoFactor).not.toHaveBeenCalled();
    expect(tabCloseAsks()).toBe(false);
    await act(async () => { finish(); });
    await screen.findByText("Login page");
    expect(codesDialog()).toBeNull();
  });
});

// A browser's fragment navigation writes a history entry with no state, which
// gives the location a new key and remounts the screen. jsdom fires no
// popstate for it, so the test pins the cause: the click is never followed.
describe("the skip link", () => {
  it("moves focus to the content without a navigation, so the screen keeps its state", async () => {
    renderApp("/form", false);
    const key = (await screen.findByTestId("location-key")).textContent;
    fireEvent.change(screen.getByLabelText("Notes"), { target: { value: "half-typed" } });

    let followed = true;
    await act(async () => { followed = fireEvent.click(skipLink()); });

    expect(followed).toBe(false);
    expect(window.location.hash).toBe("");
    expect(document.activeElement?.id).toBe("main-content");
    expect(screen.getByTestId("location-key")).toHaveTextContent(key!);
    expect(screen.getByLabelText("Notes")).toHaveValue("half-typed");
  });
});
