import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { screen, fireEvent, act, waitFor } from "@testing-library/react";
import { Routes, Route, useLocation } from "react-router";
import { Login } from "./Login";
import { ProtectedRoute } from "./ProtectedRoute";
import { RENEW_MS, RETRY_MS } from "./usePasskeySignIn";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  ApiError, login as apiLogin, completeSecondFactor as apiCompleteSecondFactor, passkeyLogin as apiPasskeyLogin,
  startPasskeySignIn, startSecondFactorPasskey,
} from "../api/client";
import type { PasskeyCeremony, SecondFactor } from "../api/client";
import { previewConsent } from "../api/oauth";
import { setStoredToken } from "../test/jwt";
import { readFarmCodes } from "../auth/farmCodeCache";
import { bindAccount, clearBoundAccount, getBoundFarmCode } from "../auth/tokenStore";
import i18n from "../i18n";

// #1285 — passkey sign-in on the sign-in page (record 1282 decisions 11 and
// 12), and the passkey answer at the second step. The browser is mocked:
// navigator.credentials.get and the PublicKeyCredential JSON helpers.
vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    login: vi.fn(),
    completeSecondFactor: vi.fn(),
    passkeyLogin: vi.fn(),
    startPasskeySignIn: vi.fn(),
    startSecondFactorPasskey: vi.fn(),
    logout: vi.fn(),
    setOnTokensChanged: vi.fn(),
    setOnUnauthenticated: vi.fn(),
  };
});
vi.mock("../api/oauth", () => ({ previewConsent: vi.fn() }));

const mockStart = vi.mocked(startPasskeySignIn);
const mockPasskeyLogin = vi.mocked(apiPasskeyLogin);
const mockLogin = vi.mocked(apiLogin);
const mockComplete = vi.mocked(apiCompleteSecondFactor);
const mockStartSecond = vi.mocked(startSecondFactorPasskey);

function ConnectProbe() {
  return <div>connect {useLocation().search}</div>;
}

function tree() {
  return (
    <Routes>
      <Route path="/login" element={<Login />} />
      <Route element={<ProtectedRoute />}>
        <Route path="/dashboard" element={<div>dashboard (protected)</div>} />
        <Route path="/connect" element={<ConnectProbe />} />
      </Route>
    </Routes>
  );
}

const ceremony = (n: number): PasskeyCeremony =>
  ({ ceremony: `ceremony-${n}`, options: { challenge: `challenge-${n}` } as PublicKeyCredentialRequestOptionsJSON });
const credential = (id: string) => ({ toJSON: () => ({ id }) });

// A conditional request stays pending until the test answers it or the page
// aborts it; a modal request answers with `modal`.
interface Pending { publicKey: unknown; signal: AbortSignal; answer: (id: string) => void }
let conditional: Pending[];
let modal: (publicKey: unknown) => Promise<unknown>;
// Like a strict browser, a modal request while autofill is pending is refused.
const get = vi.fn((init: CredentialRequestOptions) => {
  if (init.mediation !== "conditional")
    return live().length > 0 ? Promise.reject(new DOMException("Pending", "NotAllowedError")) : modal(init.publicKey);
  return new Promise((resolve, reject) => {
    const signal = init.signal!;
    conditional.push({ publicKey: init.publicKey, signal, answer: (id) => resolve(credential(id)) });
    signal.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")));
  });
});
const live = () => conditional.filter((c) => !c.signal.aborted);

function setVisibility(state: "visible" | "hidden") {
  Object.defineProperty(document, "visibilityState", { value: state, configurable: true });
  document.dispatchEvent(new Event("visibilitychange"));
}

const signedIn = async () => {
  setStoredToken({ sub: "u1", role: "Sales" });
};
// The passkey sign-in's 200 names the farm it signed in to.
const passkeySignedIn = (farm: string) => async () => {
  await signedIn();
  bindAccount("acct-A");
  return farm;
};

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(previewConsent).mockResolvedValue(null);
  localStorage.clear();
  clearBoundAccount();
  conditional = [];
  modal = async () => credential("modal-cred");
  get.mockClear();
  Object.defineProperty(navigator, "credentials", { value: { get }, configurable: true });
  vi.stubGlobal("PublicKeyCredential", {
    parseRequestOptionsFromJSON: (options: unknown) => ({ parsed: options }),
    parseCreationOptionsFromJSON: (options: unknown) => options,
    isConditionalMediationAvailable: async () => true,
  });
  let n = 0;
  mockStart.mockImplementation(async () => ceremony(++n));
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  setVisibility("visible");
});

const emailField = () => screen.getByLabelText(/Email/);
const passkeyButton = () => screen.getByRole("button", { name: i18n.t("auth:passkeySignIn") });
// The live region that announces the passkey note, when it has any text.
const announced = (text: string) => screen.getAllByRole("status").filter((r) => r.textContent === text);

async function ready(route = "/login") {
  renderWithProviders(tree(), { route, token: null });
  return screen.findByRole("button", { name: i18n.t("auth:passkeySignIn") });
}

describe("Login with a passkey (#1285)", () => {
  it("signs in from the button with a blank farm code, remembers the passkey's farm, and returns to the page asked for", async () => {
    mockPasskeyLogin.mockImplementation(passkeySignedIn("sunrise-farm"));
    await ready("/dashboard");

    await act(async () => fireEvent.click(passkeyButton()));

    expect(mockPasskeyLogin).toHaveBeenCalledWith("", { ceremony: "ceremony-1", credential: { id: "modal-cred" } });
    expect(await screen.findByText("dashboard (protected)")).toBeInTheDocument();
    // The farm comes from the passkey: the server names it.
    expect(readFarmCodes()).toEqual(["sunrise-farm"]);
    expect(getBoundFarmCode()).toBe("sunrise-farm");
  });

  it("returns to a connected app's request after a passkey sign-in", async () => {
    mockPasskeyLogin.mockImplementation(passkeySignedIn("sunrise-farm"));
    await ready("/connect?client_id=abc&scope=farm%3Aread");

    await act(async () => fireEvent.click(passkeyButton()));

    expect(await screen.findByText("connect ?client_id=abc&scope=farm%3Aread")).toBeInTheDocument();
  });

  it("sends a typed farm code and remembers the farm the server names", async () => {
    mockPasskeyLogin.mockImplementation(passkeySignedIn("farm-a"));
    await ready("/dashboard");
    fireEvent.change(screen.getByLabelText(/Farm code/), { target: { value: "Farm-A" } });

    await act(async () => fireEvent.click(passkeyButton()));

    expect(mockPasskeyLogin).toHaveBeenCalledWith("Farm-A", expect.anything());
    expect(await screen.findByText("dashboard (protected)")).toBeInTheDocument();
    expect(readFarmCodes()).toEqual(["farm-a"]);
  });

  it("offers autofill in the email field and signs in from the browser's suggestion", async () => {
    mockPasskeyLogin.mockImplementation(passkeySignedIn("sunrise-farm"));
    await ready("/dashboard");

    expect(emailField()).toHaveAttribute("autocomplete", "username webauthn");
    await waitFor(() => expect(live()).toHaveLength(1));
    expect(live()[0].publicKey).toEqual({ parsed: ceremony(1).options });

    await act(async () => live()[0].answer("autofill-cred"));

    expect(mockPasskeyLogin).toHaveBeenCalledWith("", { ceremony: "ceremony-1", credential: { id: "autofill-cred" } });
    expect(await screen.findByText("dashboard (protected)")).toBeInTheDocument();
  });

  it("stops autofill before the button's request, and starts it again after a cancel", async () => {
    modal = () => Promise.reject(new DOMException("Cancelled", "NotAllowedError"));
    await ready();
    await waitFor(() => expect(live()).toHaveLength(1));
    const first = live()[0];

    await act(async () => fireEvent.click(passkeyButton()));

    expect(first.signal.aborted).toBe(true);
    expect(await screen.findByText(i18n.t("auth:passkeySignInCancelled"))).toBeInTheDocument();
    await waitFor(() => expect(live()).toHaveLength(1));
    expect(mockPasskeyLogin).not.toHaveBeenCalled();

    // The button works again: a second press opens a second prompt.
    const prompts = get.mock.calls.filter(([init]) => init.mediation !== "conditional").length;
    await act(async () => fireEvent.click(passkeyButton()));
    expect(get.mock.calls.filter(([init]) => init.mediation !== "conditional")).toHaveLength(prompts + 1);
  });

  it("shows the one refusal message and gets a new ceremony after a refused passkey", async () => {
    mockPasskeyLogin.mockRejectedValue(new ApiError(401, "Auth.PasskeyRefused", i18n.t("errors:Auth.PasskeyRefused")));
    await ready();

    await act(async () => fireEvent.click(passkeyButton()));

    expect(await screen.findByText(i18n.t("errors:Auth.PasskeyRefused"))).toBeInTheDocument();
    await waitFor(() => expect(mockStart).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(live().map((c) => c.publicKey)).toEqual([{ parsed: ceremony(2).options }]));
  });

  it("names a suspended farm", async () => {
    mockPasskeyLogin.mockRejectedValue(new ApiError(401, "Auth.FarmSuspended", "This farm is suspended."));
    await ready();

    await act(async () => fireEvent.click(passkeyButton()));

    expect(await screen.findByText(i18n.t("auth:farmSuspended"))).toBeInTheDocument();
  });

  it("offers no passkey while passkeys are off at this address", async () => {
    mockStart.mockRejectedValue(new ApiError(404, "Passkeys.Off", i18n.t("errors:Passkeys.Off")));
    renderWithProviders(tree(), { route: "/login", token: null });

    await waitFor(() => expect(mockStart).toHaveBeenCalledTimes(1));
    await act(async () => {});
    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySignIn") })).not.toBeInTheDocument();
    expect(emailField()).toHaveAttribute("autocomplete", "username");
    expect(get).not.toHaveBeenCalled();
  });

  it("asks nothing of a browser without passkey support", async () => {
    vi.stubGlobal("PublicKeyCredential", undefined);
    renderWithProviders(tree(), { route: "/login", token: null });

    await screen.findByRole("button", { name: i18n.t("auth:signIn") });
    await act(async () => {});
    expect(mockStart).not.toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySignIn") })).not.toBeInTheDocument();
  });
});

// Decision 12: the page renews the ceremony at 4 minutes while visible, never
// while hidden, once on return, and stops autofill on a 429.
async function settle(ms = 0) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

// Under fake timers: the page renders after the session restore settles.
async function bootWithFakeTimers() {
  renderWithProviders(tree(), { route: "/dashboard", token: null });
  for (let i = 0; i < 5; i++) await settle(10);
}

async function readyWithFakeTimers() {
  await bootWithFakeTimers();
  expect(mockStart).toHaveBeenCalledTimes(1);
}

describe("Login passkey renewal (#1285)", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
  });

  it("renews at 4 minutes while visible, restarts autofill, and signs in with the new ceremony", async () => {
    mockPasskeyLogin.mockImplementation(passkeySignedIn("sunrise-farm"));
    await readyWithFakeTimers();
    expect(live()).toHaveLength(1);
    const first = live()[0];

    await settle(RENEW_MS - 1000);
    expect(mockStart).toHaveBeenCalledTimes(1);
    await settle(1000);

    expect(mockStart).toHaveBeenCalledTimes(2);
    expect(first.signal.aborted).toBe(true);
    expect(live().map((c) => c.publicKey)).toEqual([{ parsed: ceremony(2).options }]);

    await act(async () => live()[0].answer("autofill-cred"));
    expect(mockPasskeyLogin).toHaveBeenCalledWith("", { ceremony: "ceremony-2", credential: { id: "autofill-cred" } });
  });

  it("asks for no ceremony while hidden, and exactly one on return once it is 4 minutes old", async () => {
    await readyWithFakeTimers();
    setVisibility("hidden");

    // Well past due, so a renewal merely late while hidden is caught too.
    await settle(10 * 60_000);
    expect(mockStart).toHaveBeenCalledTimes(1);

    act(() => setVisibility("visible"));
    await settle();
    expect(mockStart).toHaveBeenCalledTimes(2);
    act(() => {
      setVisibility("hidden");
      setVisibility("visible");
    });
    await settle();
    expect(mockStart).toHaveBeenCalledTimes(2);
  });

  it("does not renew on return while the ceremony is younger than 4 minutes", async () => {
    await readyWithFakeTimers();
    setVisibility("hidden");
    await settle(60_000);

    act(() => setVisibility("visible"));
    await settle();
    expect(mockStart).toHaveBeenCalledTimes(1);
    await settle(RENEW_MS - 60_000);
    expect(mockStart).toHaveBeenCalledTimes(2);
  });

  it("stops autofill on a 429, disables the button with the wait, and asks again after Retry-After", async () => {
    mockStart.mockRejectedValueOnce(new ApiError(429, "Too Many Requests", "Too many.", { retryAfterSeconds: 90 }));
    await readyWithFakeTimers();

    expect(passkeyButton()).toBeDisabled();
    expect(passkeyButton()).toHaveAccessibleDescription(i18n.t("auth:passkeyLimited", { count: 2 }));
    expect(get).not.toHaveBeenCalled();
    // The password still works.
    expect(screen.getByRole("button", { name: i18n.t("auth:signIn") })).toBeEnabled();

    await settle(89_000);
    expect(mockStart).toHaveBeenCalledTimes(1);
    await settle(1000);
    expect(mockStart).toHaveBeenCalledTimes(2);
    expect(passkeyButton()).toBeEnabled();
    expect(live()).toHaveLength(1);
  });
});

describe("Login second step with a passkey (#1285)", () => {
  async function reachSecondStep(factors: SecondFactor[], route = "/login") {
    mockLogin.mockResolvedValue({ kind: "secondFactor", challenge: "challenge-jwt", expiresAt: "2099-01-01T00:00:00Z", factors });
    renderWithProviders(tree(), { route, token: null });
    await screen.findByRole("button", { name: i18n.t("auth:signIn") });
    await act(async () => {});
    fireEvent.change(screen.getByLabelText(/Farm code/), { target: { value: "default-farm" } });
    fireEvent.change(emailField(), { target: { value: "owner@farm.co" } });
    fireEvent.change(screen.getByLabelText(/Password/), { target: { value: "pw" } });
    await act(async () => fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:signIn") })));
  }

  const codeField = () => screen.queryByLabelText(new RegExp(`^${i18n.t("auth:secondFactorCode")}`));
  const usePasskey = () => screen.getByRole("button", { name: i18n.t("auth:passkeySecondSubmit") });

  it("answers a passkey-only user's second step with a passkey and returns to the page asked for", async () => {
    mockStartSecond.mockResolvedValue(ceremony(9));
    mockComplete.mockImplementation(signedIn);
    await reachSecondStep(["passkey"], "/dashboard");

    expect(screen.getByRole("heading", { name: i18n.t("auth:passkeySecondTitle") })).toBeInTheDocument();
    // Who is signing in, and where, from what the user typed.
    expect(screen.getByText("owner@farm.co")).toBeInTheDocument();
    expect(screen.getByText(i18n.t("auth:passkeySecondConfirmed", { farm: "default-farm" }))).toBeInTheDocument();
    expect(screen.getByText(i18n.t("auth:passkeySecondOnly"))).toBeInTheDocument();
    expect(codeField()).not.toBeInTheDocument();
    // Autofill stopped, so the browser can run the second step's request.
    expect(live()).toHaveLength(0);

    await act(async () => fireEvent.click(usePasskey()));

    expect(mockStartSecond).toHaveBeenCalledWith("challenge-jwt");
    expect(get).toHaveBeenLastCalledWith(expect.objectContaining({ publicKey: { parsed: ceremony(9).options } }));
    expect(mockComplete).toHaveBeenCalledWith("challenge-jwt", { ceremony: "ceremony-9", credential: { id: "modal-cred" } });
    expect(await screen.findByText("dashboard (protected)")).toBeInTheDocument();
    expect(readFarmCodes()).toEqual(["default-farm"]);
  });

  it("offers the passkey first and the code beside it for a user with both", async () => {
    mockComplete.mockImplementation(signedIn);
    await reachSecondStep(["authenticator", "passkey"]);

    expect(codeField()).not.toBeInTheDocument();
    expect(screen.queryByText(i18n.t("auth:passkeySecondOnly"))).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:passkeySecondUseCode") }));

    expect(codeField()).toBeInTheDocument();
    expect(usePasskey()).toBeInTheDocument();
    // The code view names the account and farm as the passkey view does.
    expect(screen.getByText("owner@farm.co")).toBeInTheDocument();
    expect(screen.getByText(i18n.t("auth:passkeySecondConfirmed", { farm: "default-farm" }))).toBeInTheDocument();
    fireEvent.change(codeField()!, { target: { value: "123456" } });
    await act(async () => fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:secondFactorSubmit") })));
    expect(mockComplete).toHaveBeenCalledWith("challenge-jwt", "123456");
  });

  it("asks only for the code from a user with the app alone", async () => {
    await reachSecondStep(["authenticator"]);

    expect(codeField()).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: i18n.t("auth:secondFactorTitle") })).toBeInTheDocument();
    expect(screen.getByText("owner@farm.co")).toBeInTheDocument();
    expect(screen.getByText(i18n.t("auth:passkeySecondConfirmed", { farm: "default-farm" }))).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySecondSubmit") })).not.toBeInTheDocument();
  });

  it("keeps the user on the step after a refused passkey, and after a cancel", async () => {
    mockStartSecond.mockResolvedValue(ceremony(9));
    mockComplete.mockRejectedValue(new ApiError(400, "Auth.PasskeyRefused", i18n.t("errors:Auth.PasskeyRefused")));
    await reachSecondStep(["passkey"]);

    await act(async () => fireEvent.click(usePasskey()));
    expect(await screen.findByText(i18n.t("errors:Auth.PasskeyRefused"))).toBeInTheDocument();

    modal = () => Promise.reject(new DOMException("Cancelled", "NotAllowedError"));
    await act(async () => fireEvent.click(usePasskey()));
    expect(await screen.findByText(i18n.t("auth:passkeySecondCancelled"))).toBeInTheDocument();
    expect(mockComplete).toHaveBeenCalledTimes(1);
  });

  it("sends an expired challenge back to the password step", async () => {
    mockStartSecond.mockRejectedValue(new ApiError(401, "Auth.SecondFactorChallengeExpired", "Your sign-in expired."));
    await reachSecondStep(["passkey"]);

    await act(async () => fireEvent.click(usePasskey()));

    expect(await screen.findByText(i18n.t("auth:secondFactorExpired"))).toBeInTheDocument();
    expect(screen.getByRole("button", { name: i18n.t("auth:signIn") })).toBeInTheDocument();
  });

  // Only this step's own options route says passkeys are off here (record
  // 1282 decision 5); the sign-in page's options answer decides nothing here.
  it("learns from its own options route that passkeys are off, and tells a passkey-only user so", async () => {
    mockStart.mockRejectedValue(new ApiError(503, "Service Unavailable", "Try again."));
    mockStartSecond.mockRejectedValue(new ApiError(404, "Passkeys.Off", i18n.t("errors:Passkeys.Off")));
    await reachSecondStep(["passkey"]);

    await act(async () => fireEvent.click(usePasskey()));

    expect(screen.getByText(i18n.t("auth:passkeySecondOff"))).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: i18n.t("auth:passkeySecondTitle") })).toBeInTheDocument();
    expect(screen.queryByText(i18n.t("auth:secondFactorIntro"))).not.toBeInTheDocument();
    expect(codeField()).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySecondSubmit") })).not.toBeInTheDocument();
  });

  // Maintainer, 2026-11-27: when passkeys are off, a user with the app sees
  // only the code view, with no passkey wording at all.
  const expectCodeViewOnly = () => {
    expect(codeField()).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySecondSubmit") })).not.toBeInTheDocument();
    expect(screen.queryByText(i18n.t("auth:passkeySecondIntro"))).not.toBeInTheDocument();
    expect(screen.queryByText(i18n.t("auth:passkeySecondOff"))).not.toBeInTheDocument();
    expect(screen.queryByText(i18n.t("errors:Passkeys.Off"))).not.toBeInTheDocument();
  };

  it("moves a user with the app to the code view, with no passkey wording, once its options route says passkeys are off", async () => {
    mockStartSecond.mockRejectedValue(new ApiError(404, "Passkeys.Off", i18n.t("errors:Passkeys.Off")));
    await reachSecondStep(["authenticator", "passkey"]);

    await act(async () => fireEvent.click(usePasskey()));

    expectCodeViewOnly();
  });

  it("starts a user with the app on the code view after the page's own 404 Passkeys.Off", async () => {
    mockStart.mockRejectedValue(new ApiError(404, "Passkeys.Off", i18n.t("errors:Passkeys.Off")));
    await reachSecondStep(["authenticator", "passkey"]);

    expectCodeViewOnly();
    expect(mockStartSecond).not.toHaveBeenCalled();
  });

  it("tells a passkey-only user at once, with no passkey offered, after the page's own 404 Passkeys.Off", async () => {
    mockStart.mockRejectedValue(new ApiError(404, "Passkeys.Off", i18n.t("errors:Passkeys.Off")));
    await reachSecondStep(["passkey"]);

    expect(screen.getByText(i18n.t("auth:passkeySecondOff"))).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySecondSubmit") })).not.toBeInTheDocument();
  });

  it("moves a user with the app to the code view when the page's 404 arrives while the step shows", async () => {
    const first = Promise.withResolvers<PasskeyCeremony>();
    mockStart.mockImplementationOnce(() => first.promise);
    await reachSecondStep(["authenticator", "passkey"]);
    expect(usePasskey()).toBeInTheDocument();

    await act(async () => first.reject(new ApiError(404, "Passkeys.Off", i18n.t("errors:Passkeys.Off"))));

    expectCodeViewOnly();
    expect(screen.getByRole("heading", { name: i18n.t("auth:secondFactorTitle") })).toBeInTheDocument();
  });

  it("tells a passkey-only user when the page's 404 arrives while the step shows", async () => {
    const first = Promise.withResolvers<PasskeyCeremony>();
    mockStart.mockImplementationOnce(() => first.promise);
    await reachSecondStep(["passkey"]);

    await act(async () => first.reject(new ApiError(404, "Passkeys.Off", i18n.t("errors:Passkeys.Off"))));

    expect(screen.getByText(i18n.t("auth:passkeySecondOff"))).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySecondSubmit") })).not.toBeInTheDocument();
  });

  it("keeps the step's own 404 for the page's life: back and signed in again, only the code view", async () => {
    mockStartSecond.mockRejectedValue(new ApiError(404, "Passkeys.Off", i18n.t("errors:Passkeys.Off")));
    await reachSecondStep(["authenticator", "passkey"]);
    await act(async () => fireEvent.click(usePasskey()));
    await act(async () => fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:secondFactorBack") })));

    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySignIn") })).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText(/Password/), { target: { value: "pw" } });
    await act(async () => fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:signIn") })));

    expectCodeViewOnly();
  });

  it("keeps passkeys off when the page's own request answers after the step's 404", async () => {
    const first = Promise.withResolvers<PasskeyCeremony>();
    mockStart.mockImplementationOnce(() => first.promise);
    mockStartSecond.mockRejectedValue(new ApiError(404, "Passkeys.Off", i18n.t("errors:Passkeys.Off")));
    await reachSecondStep(["authenticator", "passkey"]);
    await act(async () => fireEvent.click(usePasskey()));

    await act(async () => first.resolve(ceremony(1)));
    await act(async () => fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:secondFactorBack") })));

    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySignIn") })).not.toBeInTheDocument();
  });

  it.each([
    ["a 429", () => new ApiError(429, "Too Many Requests", "Too many.", { retryAfterSeconds: 90 })],
    ["a 503", () => new ApiError(503, "Service Unavailable", "Try again.")],
    ["a network error", () => new TypeError("Failed to fetch")],
  ])("keeps passkeys off when the page's own request fails with %s after the step's 404", async (_, failure) => {
    const first = Promise.withResolvers<PasskeyCeremony>();
    mockStart.mockImplementationOnce(() => first.promise);
    mockStartSecond.mockRejectedValue(new ApiError(404, "Passkeys.Off", i18n.t("errors:Passkeys.Off")));
    await reachSecondStep(["authenticator", "passkey"]);
    await act(async () => fireEvent.click(usePasskey()));

    await act(async () => first.reject(failure()));
    expectCodeViewOnly();
    await act(async () => fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:secondFactorBack") })));

    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySignIn") })).not.toBeInTheDocument();
  });

  it("takes only a 404 Passkeys.Off at the step as off, not any 404", async () => {
    mockStartSecond.mockRejectedValue(new ApiError(404, "Not Found", "Not found."));
    await reachSecondStep(["authenticator", "passkey"]);

    await act(async () => fireEvent.click(usePasskey()));

    expect(usePasskey()).toBeInTheDocument();
    expect(codeField()).not.toBeInTheDocument();
  });

  it("keeps the step's 404 for the page's life when the page was limited by a 429", async () => {
    mockStart.mockRejectedValueOnce(new ApiError(429, "Too Many Requests", "Too many.", { retryAfterSeconds: 90 }));
    mockStartSecond.mockRejectedValue(new ApiError(404, "Passkeys.Off", i18n.t("errors:Passkeys.Off")));
    await reachSecondStep(["authenticator", "passkey"]);
    await act(async () => fireEvent.click(usePasskey()));
    await act(async () => fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:secondFactorBack") })));

    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySignIn") })).not.toBeInTheDocument();
    fireEvent.change(screen.getByLabelText(/Password/), { target: { value: "pw" } });
    await act(async () => fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:signIn") })));

    expectCodeViewOnly();
  });

  it("still offers the passkey at the step after a 429 at the page", async () => {
    mockStart.mockRejectedValueOnce(new ApiError(429, "Too Many Requests", "Too many.", { retryAfterSeconds: 90 }));
    await reachSecondStep(["passkey"]);

    expect(usePasskey()).toBeEnabled();
    expect(screen.queryByText(i18n.t("auth:passkeySecondOff"))).not.toBeInTheDocument();
  });

  it("starts a user with the app on the code view in a browser without passkeys", async () => {
    vi.stubGlobal("PublicKeyCredential", undefined);
    await reachSecondStep(["authenticator", "passkey"]);

    expectCodeViewOnly();
    expect(screen.queryByText(i18n.t("auth:passkeySecondUnsupported"))).not.toBeInTheDocument();
  });
});

async function passwordStep(factors: SecondFactor[]) {
  mockLogin.mockResolvedValue({ kind: "secondFactor", challenge: "challenge-jwt", expiresAt: "2099-01-01T00:00:00Z", factors });
  fireEvent.change(screen.getByLabelText(/Farm code/), { target: { value: "default-farm" } });
  fireEvent.change(emailField(), { target: { value: "owner@farm.co" } });
  fireEvent.change(screen.getByLabelText(/Password/), { target: { value: "pw" } });
  await act(async () => fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:signIn") })));
}

// Record 1282: only the options route's 404 Passkeys.Off means passkeys are
// off. A failed request is not that, and neither is a browser that cannot.
describe("Login passkey when a request or the browser fails (#1285)", () => {
  it.each([
    ["a 503", new ApiError(503, "Service Unavailable", "Try again.")],
    ["a network error", new TypeError("Failed to fetch")],
  ])("after %s at page load, a passkey-only user still answers the second step with a passkey", async (_, failure) => {
    mockStart.mockRejectedValueOnce(failure);
    renderWithProviders(tree(), { route: "/login", token: null });
    await waitFor(() => expect(mockStart).toHaveBeenCalled());
    await passwordStep(["passkey"]);

    expect(screen.queryByText(i18n.t("auth:passkeySecondOff"))).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: i18n.t("auth:passkeySecondSubmit") })).toBeInTheDocument();
  });

  it("tells a passkey-only user in a browser without passkeys to use another browser, not that passkeys are off", async () => {
    vi.stubGlobal("PublicKeyCredential", undefined);
    renderWithProviders(tree(), { route: "/login", token: null });
    await screen.findByRole("button", { name: i18n.t("auth:signIn") });
    await passwordStep(["passkey"]);

    expect(screen.getByText(i18n.t("auth:passkeySecondUnsupported"))).toBeInTheDocument();
    expect(screen.queryByText(i18n.t("auth:passkeySecondOff"))).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySecondSubmit") })).not.toBeInTheDocument();
  });

  it("keeps the passkey button after an offline attempt, disabled until a ceremony arrives", async () => {
    mockPasskeyLogin.mockRejectedValue(new TypeError("Failed to fetch"));
    await ready();
    mockStart.mockRejectedValueOnce(new TypeError("Failed to fetch"));

    await act(async () => fireEvent.click(passkeyButton()));

    expect(passkeyButton()).toBeDisabled();
  });

  it("names a browser or authenticator failure the same way at the button and at the second step", async () => {
    modal = () => Promise.reject(new DOMException("The relying party is not valid.", "SecurityError"));
    await ready();
    await act(async () => fireEvent.click(passkeyButton()));
    expect(await screen.findByText(i18n.t("auth:passkeyBrowserFailed"))).toBeInTheDocument();

    mockStartSecond.mockResolvedValue(ceremony(9));
    await passwordStep(["passkey"]);
    await act(async () => fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:passkeySecondSubmit") })));
    expect(screen.getByRole("alert")).toHaveTextContent(i18n.t("auth:passkeyBrowserFailed"));
    expect(mockComplete).not.toHaveBeenCalled();
  });
});

// Record 1282 decision 12: a refused ceremony is never sent again, and the
// page gets a new one and asks the user to try again.
describe("Login passkey after a refusal (#1285)", () => {
  const refused = () => new ApiError(401, "Auth.PasskeyRefused", i18n.t("errors:Auth.PasskeyRefused"));

  it("disables the button and stops autofill until the new ceremony arrives", async () => {
    mockPasskeyLogin.mockRejectedValue(refused());
    await ready();
    const slow = Promise.withResolvers<PasskeyCeremony>();
    mockStart.mockImplementationOnce(() => slow.promise);

    await act(async () => fireEvent.click(passkeyButton()));
    expect(await screen.findByText(i18n.t("errors:Auth.PasskeyRefused"))).toBeInTheDocument();
    expect(passkeyButton()).toBeDisabled();
    expect(live()).toHaveLength(0);

    await act(async () => slow.resolve(ceremony(2)));
    expect(passkeyButton()).toBeEnabled();
    expect(live().map((c) => c.publicKey)).toEqual([{ parsed: ceremony(2).options }]);
    expect(mockPasskeyLogin.mock.calls.map(([, answer]) => answer.ceremony)).toEqual(["ceremony-1"]);
  });

  it("does not answer itself again and again with an authenticator that answers autofill at once", async () => {
    mockPasskeyLogin.mockRejectedValue(refused());
    get.mockImplementation((init: CredentialRequestOptions) =>
      init.mediation === "conditional" ? Promise.resolve(credential("eager")) : modal(init.publicKey));
    renderWithProviders(tree(), { route: "/login", token: null });

    await waitFor(() => expect(mockPasskeyLogin).toHaveBeenCalled());
    await act(async () => new Promise((r) => setTimeout(r, 200)));

    // One answer, refused; autofill then waits for the user.
    expect(mockPasskeyLogin).toHaveBeenCalledTimes(1);
  });

  it("lets autofill run again once the user goes back to the email field", async () => {
    mockPasskeyLogin.mockRejectedValue(refused());
    await ready();
    await waitFor(() => expect(live()).toHaveLength(1));
    await act(async () => live()[0].answer("autofill-cred"));
    await screen.findByText(i18n.t("errors:Auth.PasskeyRefused"));
    await waitFor(() => expect(passkeyButton()).toBeEnabled());
    expect(live()).toHaveLength(0);

    fireEvent.focus(emailField());

    await waitFor(() => expect(live()).toHaveLength(1));
  });

  it("asks for no ceremony after a successful sign-in", async () => {
    mockPasskeyLogin.mockImplementation(passkeySignedIn("sunrise-farm"));
    await ready("/dashboard");

    await act(async () => fireEvent.click(passkeyButton()));
    expect(await screen.findByText("dashboard (protected)")).toBeInTheDocument();

    expect(mockStart).toHaveBeenCalledTimes(1);
  });
});

describe("Login passkey and password, one at a time (#1285)", () => {
  it("cannot submit the password while a passkey sign-in is in flight", async () => {
    mockPasskeyLogin.mockImplementation(() => new Promise(() => {}));
    await ready();
    fireEvent.change(screen.getByLabelText(/Farm code/), { target: { value: "default-farm" } });
    fireEvent.change(emailField(), { target: { value: "owner@farm.co" } });
    fireEvent.change(screen.getByLabelText(/Password/), { target: { value: "pw" } });
    await act(async () => fireEvent.click(passkeyButton()));

    await act(async () => fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:signIn") })));

    expect(mockLogin).not.toHaveBeenCalled();
  });

  it("stops autofill while the password is being checked", async () => {
    mockLogin.mockImplementation(() => new Promise(() => {}));
    await ready();
    await waitFor(() => expect(live()).toHaveLength(1));
    fireEvent.change(screen.getByLabelText(/Farm code/), { target: { value: "default-farm" } });
    fireEvent.change(emailField(), { target: { value: "owner@farm.co" } });
    fireEvent.change(screen.getByLabelText(/Password/), { target: { value: "pw" } });

    await act(async () => fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:signIn") })));

    expect(live()).toHaveLength(0);
    expect(passkeyButton()).toBeDisabled();
  });
});

describe("Login passkey requests (#1285)", () => {
  it("asks once while a renewal is out, however often the page is shown again", async () => {
    mockStart.mockImplementation(() => new Promise(() => {}));
    renderWithProviders(tree(), { route: "/login", token: null });
    await waitFor(() => expect(mockStart).toHaveBeenCalled());

    for (let i = 0; i < 5; i++) act(() => setVisibility("visible"));
    await act(async () => new Promise((r) => setTimeout(r, 50)));

    expect(mockStart).toHaveBeenCalledTimes(1);
  });

  it("ties the disabled button to the note that says why, and announces the note", async () => {
    mockStart.mockRejectedValueOnce(new ApiError(429, "Too Many Requests", "Too many.", { retryAfterSeconds: 90 }));
    renderWithProviders(tree(), { route: "/login", token: null });
    const note = i18n.t("auth:passkeyLimited", { count: 2 });

    await waitFor(() => expect(passkeyButton()).toHaveAccessibleDescription(note));

    expect(announced(note)).toHaveLength(1);
    // A sighted user sees it too.
    expect(screen.getByText(note, { selector: "p" })).toBeVisible();
  });
});

describe("Login passkey timing (#1285)", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
  });

  it("hides the button after a failed first request, and asks again after a pause", async () => {
    mockStart.mockRejectedValueOnce(new TypeError("Failed to fetch"));
    await bootWithFakeTimers();
    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySignIn") })).not.toBeInTheDocument();

    await settle(RETRY_MS);

    expect(mockStart).toHaveBeenCalledTimes(2);
    expect(passkeyButton()).toBeEnabled();
  });

  it("announces a second 429 with the same wait, clearing the note while the request is out", async () => {
    const first = Promise.withResolvers<PasskeyCeremony>();
    const second = Promise.withResolvers<PasskeyCeremony>();
    mockStart.mockImplementationOnce(() => first.promise).mockImplementationOnce(() => second.promise);
    await bootWithFakeTimers();
    const regions = screen.getAllByRole("status");
    const said: string[] = [];
    const observer = new MutationObserver(() => {
      const text = regions.map((r) => r.textContent).join("");
      if (said.at(-1) !== text) said.push(text);
    });
    regions.forEach((r) => observer.observe(r, { childList: true, characterData: true, subtree: true }));
    const limit = () => new ApiError(429, "Too Many Requests", "Too many.", { retryAfterSeconds: 90 });
    const note = i18n.t("auth:passkeyLimited", { count: 2 });

    await act(async () => first.reject(limit()));
    await settle(91_000);
    // While the request is out, the button stays, disabled, and says why.
    expect(passkeyButton()).toBeDisabled();
    expect(passkeyButton()).toHaveAccessibleDescription(i18n.t("auth:passkeyRenewing"));
    await act(async () => second.reject(limit()));
    observer.disconnect();

    expect(said).toEqual([note, "", note]);
  });

  it("waits out at most the policy's window, whatever Retry-After says", async () => {
    mockStart.mockRejectedValueOnce(new ApiError(429, "Too Many Requests", "Too many.", { retryAfterSeconds: 3_000_000 }));
    await bootWithFakeTimers();
    expect(passkeyButton()).toHaveAccessibleDescription(i18n.t("auth:passkeyLimited", { count: 15 }));

    await settle(899_000);
    expect(mockStart).toHaveBeenCalledTimes(1);
    await settle(1000);
    expect(mockStart).toHaveBeenCalledTimes(2);
  });
});

describe("Login passkey autofill after a refused answer (#1285)", () => {
  async function refusedAutofill() {
    mockPasskeyLogin.mockRejectedValue(new ApiError(401, "Auth.PasskeyRefused", i18n.t("errors:Auth.PasskeyRefused")));
    await ready();
    await waitFor(() => expect(live()).toHaveLength(1));
    await act(async () => live()[0].answer("autofill-cred"));
    await screen.findByText(i18n.t("errors:Auth.PasskeyRefused"));
    await waitFor(() => expect(passkeyButton()).toBeEnabled());
    expect(live()).toHaveLength(0);
  }

  it("comes back on a press in the email field, which may still have focus", async () => {
    await refusedAutofill();
    fireEvent.pointerDown(emailField());
    await waitFor(() => expect(live()).toHaveLength(1));
  });

  it("comes back when the user types in the email field", async () => {
    await refusedAutofill();
    fireEvent.change(emailField(), { target: { value: "o" } });
    await waitFor(() => expect(live()).toHaveLength(1));
  });

  it("comes back after the user presses the passkey button", async () => {
    await refusedAutofill();
    modal = () => Promise.reject(new DOMException("Cancelled", "NotAllowedError"));
    await act(async () => fireEvent.click(passkeyButton()));
    await screen.findByText(i18n.t("auth:passkeySignInCancelled"));
    await waitFor(() => expect(live()).toHaveLength(1));
  });
});

describe("Login passkey note (#1285)", () => {
  it("says why the button is disabled while a new ceremony is on its way", async () => {
    mockPasskeyLogin.mockRejectedValue(new ApiError(401, "Auth.PasskeyRefused", i18n.t("errors:Auth.PasskeyRefused")));
    await ready();
    const next = Promise.withResolvers<PasskeyCeremony>();
    mockStart.mockImplementationOnce(() => next.promise);

    await act(async () => fireEvent.click(passkeyButton()));

    expect(passkeyButton()).toBeDisabled();
    expect(passkeyButton()).toHaveAccessibleDescription(i18n.t("auth:passkeyRenewing"));
    await act(async () => next.resolve(ceremony(2)));
    expect(passkeyButton()).toHaveAccessibleDescription(i18n.t("auth:passkeyFarmCodeOptional"));
  });

  it("keeps its live region in the page before the first answer, so a 429 at load is announced", async () => {
    const first = Promise.withResolvers<PasskeyCeremony>();
    mockStart.mockImplementationOnce(() => first.promise);
    renderWithProviders(tree(), { route: "/login", token: null });
    await screen.findByRole("button", { name: i18n.t("auth:signIn") });
    const regions = screen.getAllByRole("status");

    await act(async () => first.reject(new ApiError(429, "Too Many Requests", "Too many.", { retryAfterSeconds: 90 })));

    const [region] = announced(i18n.t("auth:passkeyLimited", { count: 2 }));
    expect(regions).toContain(region);
  });
});

describe("Login passkey ceremony age (#1285)", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
  });

  it("offers no expired ceremony on return while the new one is on its way", async () => {
    await readyWithFakeTimers();
    setVisibility("hidden");
    await settle(6 * 60_000);
    const next = Promise.withResolvers<PasskeyCeremony>();
    mockStart.mockImplementationOnce(() => next.promise);

    act(() => setVisibility("visible"));
    await settle();

    expect(passkeyButton()).toBeDisabled();
    expect(live()).toHaveLength(0);
    await act(async () => next.resolve(ceremony(2)));
    expect(passkeyButton()).toBeEnabled();
    expect(live().map((c) => c.publicKey)).toEqual([{ parsed: ceremony(2).options }]);
  });

  it("disables the button and stops autofill when a renewal fails", async () => {
    await readyWithFakeTimers();
    mockStart.mockRejectedValueOnce(new TypeError("Failed to fetch"));

    await settle(RENEW_MS);

    expect(passkeyButton()).toBeDisabled();
    expect(live()).toHaveLength(0);
  });
});

describe("Login passkey announcements and keys (#1285)", () => {
  it("announces nothing on an ordinary load or a refusal, only the 429 note", async () => {
    const first = Promise.withResolvers<PasskeyCeremony>();
    mockStart.mockImplementationOnce(() => first.promise);
    renderWithProviders(tree(), { route: "/login", token: null });
    await screen.findByRole("button", { name: i18n.t("auth:signIn") });
    const regions = screen.getAllByRole("status");
    const said: string[] = [];
    const observer = new MutationObserver(() => regions.forEach((r) => r.textContent && said.push(r.textContent)));
    regions.forEach((r) => observer.observe(r, { childList: true, characterData: true, subtree: true }));

    await act(async () => first.resolve(ceremony(1)));
    mockPasskeyLogin.mockRejectedValue(new ApiError(401, "Auth.PasskeyRefused", i18n.t("errors:Auth.PasskeyRefused")));
    await act(async () => fireEvent.click(passkeyButton()));
    await waitFor(() => expect(passkeyButton()).toBeEnabled());
    observer.disconnect();

    expect(said).not.toContain(i18n.t("auth:passkeyFarmCodeOptional"));
    expect(said).not.toContain(i18n.t("auth:passkeyRenewing"));
  });

  it("lets a held key lift the autofill pause once, not on every repeat", async () => {
    mockPasskeyLogin.mockRejectedValue(new ApiError(401, "Auth.PasskeyRefused", i18n.t("errors:Auth.PasskeyRefused")));
    await ready();
    await waitFor(() => expect(live()).toHaveLength(1));
    await act(async () => live()[0].answer("autofill-cred"));
    await waitFor(() => expect(passkeyButton()).toBeEnabled());

    fireEvent.keyDown(emailField(), { key: "Shift", repeat: true });
    await act(async () => {});

    expect(live()).toHaveLength(0);
  });

  it("lets autofill run again when a keyboard user presses a key in the focused email field", async () => {
    mockPasskeyLogin.mockRejectedValue(new ApiError(401, "Auth.PasskeyRefused", i18n.t("errors:Auth.PasskeyRefused")));
    await ready();
    await waitFor(() => expect(live()).toHaveLength(1));
    await act(async () => live()[0].answer("autofill-cred"));
    await waitFor(() => expect(passkeyButton()).toBeEnabled());
    expect(live()).toHaveLength(0);

    fireEvent.keyDown(emailField(), { key: "ArrowDown" });

    await waitFor(() => expect(live()).toHaveLength(1));
  });
});

describe("Login passkey renewal that hangs (#1285)", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
  });

  it("stops offering the ceremony at 5 minutes while its renewal hangs on a visible page", async () => {
    await readyWithFakeTimers();
    mockStart.mockImplementationOnce(() => new Promise(() => {}));
    await settle(RENEW_MS);
    expect(mockStart).toHaveBeenCalledTimes(2);
    expect(passkeyButton()).toBeEnabled();

    await settle(90_000);

    expect(passkeyButton()).toBeDisabled();
    expect(live()).toHaveLength(0);
  });

  it("offers no expired ceremony on return while a renewal from before the page was hidden still hangs", async () => {
    await readyWithFakeTimers();
    mockStart.mockImplementationOnce(() => new Promise(() => {}));
    await settle(RENEW_MS);
    setVisibility("hidden");
    await settle(6 * 60_000);

    act(() => setVisibility("visible"));
    await settle();

    expect(passkeyButton()).toBeDisabled();
    expect(live()).toHaveLength(0);
  });
});

// Record 1282 decision 11: the farm code is the one in the field when the
// answer is sent, however long the browser took to give it.
describe("Login passkey farm code at the time of the answer (#1285)", () => {
  const pendingAutofill = async () => {
    await waitFor(() => expect(live()).toHaveLength(1));
    return live()[0];
  };

  it("sends a farm code typed after autofill started", async () => {
    mockPasskeyLogin.mockImplementation(passkeySignedIn("farm-b"));
    await ready("/dashboard");
    const pending = await pendingAutofill();

    fireEvent.change(screen.getByLabelText(/Farm code/), { target: { value: "farm-b" } });
    await act(async () => pending.answer("autofill-cred"));

    expect(mockPasskeyLogin).toHaveBeenCalledWith("farm-b", { ceremony: "ceremony-1", credential: { id: "autofill-cred" } });
  });

  it("sends a farm picked from Recent farms after autofill started", async () => {
    localStorage.setItem("cluckwork.farmCodes", JSON.stringify(["farm-a", "farm-b"]));
    mockPasskeyLogin.mockImplementation(passkeySignedIn("farm-b"));
    await ready("/dashboard");
    const pending = await pendingAutofill();

    fireEvent.click(screen.getByRole("button", { name: "farm-b" }));
    await act(async () => pending.answer("autofill-cred"));

    expect(mockPasskeyLogin).toHaveBeenCalledWith("farm-b", expect.anything());
  });

  it("sends a blank farm code when a prefilled one is cleared after autofill started", async () => {
    localStorage.setItem("cluckwork.farmCodes", JSON.stringify(["farm-a"]));
    mockPasskeyLogin.mockImplementation(passkeySignedIn("farm-b"));
    await ready("/dashboard");
    const pending = await pendingAutofill();
    expect(screen.getByLabelText(/Farm code/)).toHaveValue("farm-a");

    fireEvent.change(screen.getByLabelText(/Farm code/), { target: { value: "" } });
    await act(async () => pending.answer("autofill-cred"));

    expect(mockPasskeyLogin).toHaveBeenCalledWith("", expect.anything());
  });

  it("sends the farm code in the field when the button's prompt is answered", async () => {
    const prompt = Promise.withResolvers<unknown>();
    modal = () => prompt.promise;
    mockPasskeyLogin.mockImplementation(passkeySignedIn("farm-b"));
    await ready("/dashboard");
    await act(async () => fireEvent.click(passkeyButton()));

    fireEvent.change(screen.getByLabelText(/Farm code/), { target: { value: "farm-b" } });
    await act(async () => prompt.resolve(credential("modal-cred")));

    expect(mockPasskeyLogin).toHaveBeenCalledWith("farm-b", expect.anything());
  });
});

describe("Login passkey sign-in in flight (#1285)", () => {
  it("turns passkeys off when the sign-in itself answers 404 Passkeys.Off", async () => {
    mockPasskeyLogin.mockRejectedValueOnce(new ApiError(404, "Passkeys.Off", i18n.t("errors:Passkeys.Off")));
    await ready();

    await act(async () => fireEvent.click(passkeyButton()));
    await act(async () => {});

    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySignIn") })).not.toBeInTheDocument();
    expect(mockStart).toHaveBeenCalledTimes(1);
  });

  it("does not let an aborted autofill answer sign in beside the password", async () => {
    mockLogin.mockImplementation(() => new Promise(() => {}));
    mockPasskeyLogin.mockImplementation(() => new Promise(() => {}));
    await ready();
    await waitFor(() => expect(live()).toHaveLength(1));
    fireEvent.change(screen.getByLabelText(/Farm code/), { target: { value: "default-farm" } });
    fireEvent.change(emailField(), { target: { value: "owner@farm.co" } });
    fireEvent.change(screen.getByLabelText(/Password/), { target: { value: "pw" } });
    const pending = live()[0];

    await act(async () => {
      pending.answer("autofill-cred");
      fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:signIn") }));
    });

    expect(pending.signal.aborted).toBe(true);
    expect(mockLogin).toHaveBeenCalledTimes(1);
    expect(mockPasskeyLogin).not.toHaveBeenCalled();
  });

  it("opens one prompt for two clicks in the same tick", async () => {
    const prompt = Promise.withResolvers<unknown>();
    let prompts = 0;
    modal = () => {
      prompts += 1;
      return prompts === 1 ? prompt.promise : Promise.reject(new DOMException("A request is already pending.", "OperationError"));
    };
    await ready();
    await waitFor(() => expect(live()).toHaveLength(1));

    const button = passkeyButton();
    await act(async () => {
      button.click();
      button.click();
    });

    expect(prompts).toBe(1);
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });
});

describe("Login passkey renewal around a sign-in (#1285)", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
  });

  it.each(["the button", "autofill"])("asks for no ceremony while a passkey sign-in from %s is in flight, and renews once it is refused", async (path) => {
    const signIn = Promise.withResolvers<string>();
    mockPasskeyLogin.mockImplementationOnce(() => signIn.promise);
    await readyWithFakeTimers();
    await settle(RENEW_MS - 1000);

    if (path === "autofill") await act(async () => live()[0].answer("autofill-cred"));
    else await act(async () => fireEvent.click(passkeyButton()));
    expect(mockPasskeyLogin).toHaveBeenCalledTimes(1);
    await settle(2000);
    expect(mockStart).toHaveBeenCalledTimes(1);

    await act(async () => signIn.reject(new ApiError(401, "Auth.PasskeyRefused", i18n.t("errors:Auth.PasskeyRefused"))));
    await settle(10);

    expect(mockStart).toHaveBeenCalledTimes(2);
    expect(passkeyButton()).toBeEnabled();
  });

  it.each([
    ["404 Passkeys.Off", () => new ApiError(404, "Passkeys.Off", i18n.t("errors:Passkeys.Off")), false],
    ["429", () => new ApiError(429, "Too Many Requests", "Too many.", { retryAfterSeconds: 300 }), true],
  ])("keeps what a renewal's %s said when the sign-in answering after it is refused", async (_, failure, offered) => {
    const prompt = Promise.withResolvers<unknown>();
    modal = () => prompt.promise;
    mockPasskeyLogin.mockRejectedValue(new ApiError(401, "Auth.PasskeyRefused", i18n.t("errors:Auth.PasskeyRefused")));
    await readyWithFakeTimers();
    const renewal = Promise.withResolvers<PasskeyCeremony>();
    mockStart.mockImplementationOnce(() => renewal.promise);
    await settle(RENEW_MS);
    expect(mockStart).toHaveBeenCalledTimes(2);

    await act(async () => fireEvent.click(passkeyButton()));
    await act(async () => renewal.reject(failure()));
    await act(async () => prompt.resolve(credential("modal-cred")));
    await settle(10);

    expect(mockStart).toHaveBeenCalledTimes(2);
    expect(screen.queryAllByRole("button", { name: i18n.t("auth:passkeySignIn") }).length > 0).toBe(offered);
    if (offered) expect(passkeyButton()).toHaveAccessibleDescription(i18n.t("auth:passkeyLimited", { count: 5 }));
  });

  // A 429 and the sign-in's own 404 Passkeys.Off: off wins.
  it("turns passkeys off when the sign-in answers 404 Passkeys.Off after a renewal's 429", async () => {
    const prompt = Promise.withResolvers<unknown>();
    modal = () => prompt.promise;
    mockPasskeyLogin.mockRejectedValueOnce(new ApiError(404, "Passkeys.Off", i18n.t("errors:Passkeys.Off")));
    await readyWithFakeTimers();
    const renewal = Promise.withResolvers<PasskeyCeremony>();
    mockStart.mockImplementationOnce(() => renewal.promise);
    await settle(RENEW_MS);

    await act(async () => fireEvent.click(passkeyButton()));
    await act(async () => renewal.reject(new ApiError(429, "Too Many Requests", "Too many.", { retryAfterSeconds: 300 })));
    await act(async () => prompt.resolve(credential("modal-cred")));
    await settle(301_000);

    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySignIn") })).not.toBeInTheDocument();
    expect(mockStart).toHaveBeenCalledTimes(2);
  });

  // The clock moved past the ceremony with no timer run, as after a sleep.
  it("opens no prompt for a ceremony the clock has expired before the expiry timer ran", async () => {
    await readyWithFakeTimers();
    mockStart.mockImplementationOnce(() => new Promise(() => {}));
    vi.setSystemTime(Date.now() + 6 * 60_000);

    await act(async () => fireEvent.click(passkeyButton()));

    expect(get.mock.calls.filter(([init]) => init.mediation !== "conditional")).toHaveLength(0);
    expect(passkeyButton()).toBeDisabled();
  });

  // An autofill answer and a press before React renders again.
  it("lets the button start nothing while an autofill sign-in is out", async () => {
    mockPasskeyLogin.mockImplementation(() => new Promise(() => {}));
    await readyWithFakeTimers();
    const button = passkeyButton();

    await act(async () => {
      live()[0].answer("autofill-cred");
      for (let i = 0; i < 10 && mockPasskeyLogin.mock.calls.length === 0; i++) await Promise.resolve();
      expect(mockPasskeyLogin).toHaveBeenCalledTimes(1);
      button.click();
    });

    expect(mockPasskeyLogin).toHaveBeenCalledTimes(1);
    expect(get.mock.calls.filter(([init]) => init.mediation !== "conditional")).toHaveLength(0);
  });

  it("offers no expired ceremony, even for a moment, on return from a long second step", async () => {
    mockLogin.mockResolvedValue({ kind: "secondFactor", challenge: "challenge-jwt", expiresAt: "2099-01-01T00:00:00Z", factors: ["passkey"] });
    await readyWithFakeTimers();
    expect(get).toHaveBeenCalledTimes(1);
    fireEvent.change(screen.getByLabelText(/Farm code/), { target: { value: "default-farm" } });
    fireEvent.change(emailField(), { target: { value: "owner@farm.co" } });
    fireEvent.change(screen.getByLabelText(/Password/), { target: { value: "pw" } });
    await act(async () => fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:signIn") })));
    await settle(5.5 * 60_000);
    mockStart.mockImplementationOnce(() => new Promise(() => {}));

    // Microtasks only: the expiry timer has not run yet.
    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:secondFactorBack") }));
      await Promise.resolve();
    });

    expect(get).toHaveBeenCalledTimes(1);
    expect(passkeyButton()).toBeDisabled();
  });
});
