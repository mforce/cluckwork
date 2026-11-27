import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { screen, fireEvent, act, waitFor } from "@testing-library/react";
import { Routes, Route, useLocation } from "react-router";
import { Login } from "./Login";
import { ProtectedRoute } from "./ProtectedRoute";
import { RENEW_MS } from "./usePasskeySignIn";
import { renderWithProviders } from "../test/renderWithProviders";
import {
  ApiError, login as apiLogin, completeSecondFactor as apiCompleteSecondFactor, passkeyLogin as apiPasskeyLogin,
  startPasskeySignIn, startSecondFactorPasskey,
} from "../api/client";
import type { PasskeyCeremony, SecondFactor } from "../api/client";
import { previewConsent } from "../api/oauth";
import { setStoredToken } from "../test/jwt";
import { readFarmCodes } from "../auth/farmCodeCache";
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
const get = vi.fn((init: CredentialRequestOptions) => {
  if (init.mediation !== "conditional") return modal(init.publicKey);
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

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(previewConsent).mockResolvedValue(null);
  localStorage.clear();
  conditional = [];
  modal = async () => credential("modal-cred");
  get.mockClear();
  Object.defineProperty(navigator, "credentials", { value: { get }, configurable: true });
  vi.stubGlobal("PublicKeyCredential", {
    parseRequestOptionsFromJSON: (options: unknown) => ({ parsed: options }),
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

async function ready(route = "/login") {
  renderWithProviders(tree(), { route, token: null });
  return screen.findByRole("button", { name: i18n.t("auth:passkeySignIn") });
}

describe("Login with a passkey (#1285)", () => {
  it("signs in from the button with a blank farm code and returns to the page asked for", async () => {
    mockPasskeyLogin.mockImplementation(signedIn);
    await ready("/dashboard");

    await act(async () => fireEvent.click(passkeyButton()));

    expect(mockPasskeyLogin).toHaveBeenCalledWith({ ceremony: "ceremony-1", credential: { id: "modal-cred" } }, "");
    expect(await screen.findByText("dashboard (protected)")).toBeInTheDocument();
    // A blank farm code names no farm, so none is remembered.
    expect(readFarmCodes()).toEqual([]);
  });

  it("returns to a connected app's request after a passkey sign-in", async () => {
    mockPasskeyLogin.mockImplementation(signedIn);
    await ready("/connect?client_id=abc&scope=farm%3Aread");

    await act(async () => fireEvent.click(passkeyButton()));

    expect(await screen.findByText("connect ?client_id=abc&scope=farm%3Aread")).toBeInTheDocument();
  });

  it("sends a typed farm code and remembers the farm after the sign-in", async () => {
    mockPasskeyLogin.mockImplementation(signedIn);
    await ready("/dashboard");
    fireEvent.change(screen.getByLabelText(/Farm code/), { target: { value: "Farm-A" } });

    await act(async () => fireEvent.click(passkeyButton()));

    expect(mockPasskeyLogin).toHaveBeenCalledWith(expect.anything(), "Farm-A");
    expect(await screen.findByText("dashboard (protected)")).toBeInTheDocument();
    expect(readFarmCodes()).toEqual(["farm-a"]);
  });

  it("offers autofill in the email field and signs in from the browser's suggestion", async () => {
    mockPasskeyLogin.mockImplementation(signedIn);
    await ready("/dashboard");

    expect(emailField()).toHaveAttribute("autocomplete", "username webauthn");
    await waitFor(() => expect(live()).toHaveLength(1));
    expect(live()[0].publicKey).toEqual({ parsed: ceremony(1).options });

    await act(async () => live()[0].answer("autofill-cred"));

    expect(mockPasskeyLogin).toHaveBeenCalledWith({ ceremony: "ceremony-1", credential: { id: "autofill-cred" } }, "");
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
  });

  it("shows the one refusal message and gets a new ceremony after a refused passkey", async () => {
    mockPasskeyLogin.mockRejectedValue(new ApiError(401, "Auth.PasskeyRefused", "That passkey did not work. Try again."));
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
    mockStart.mockRejectedValue(new ApiError(404, "Passkeys.Off", "Passkeys are off."));
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
describe("Login passkey renewal (#1285)", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
  });

  async function settle(ms = 0) {
    await act(async () => {
      await vi.advanceTimersByTimeAsync(ms);
    });
  }

  async function readyWithFakeTimers() {
    renderWithProviders(tree(), { route: "/dashboard", token: null });
    // The page renders after the session restore settles.
    for (let i = 0; i < 5; i++) await settle(10);
    expect(mockStart).toHaveBeenCalledTimes(1);
  }

  it("renews at 4 minutes while visible, restarts autofill, and signs in with the new ceremony", async () => {
    mockPasskeyLogin.mockImplementation(signedIn);
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
    expect(mockPasskeyLogin).toHaveBeenCalledWith({ ceremony: "ceremony-2", credential: { id: "autofill-cred" } }, "");
  });

  it("asks for no ceremony while hidden, and exactly one on return once it is 4 minutes old", async () => {
    await readyWithFakeTimers();
    setVisibility("hidden");

    await settle(RENEW_MS + 30_000);
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
    expect(screen.getByText(i18n.t("auth:passkeyLimited", { count: 2 }))).toBeInTheDocument();
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
    await ready(route);
    await waitFor(() => expect(live()).toHaveLength(1));
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
    fireEvent.change(codeField()!, { target: { value: "123456" } });
    await act(async () => fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:secondFactorSubmit") })));
    expect(mockComplete).toHaveBeenCalledWith("challenge-jwt", "123456");
  });

  it("asks only for the code from a user with the app alone", async () => {
    await reachSecondStep(["authenticator"]);

    expect(codeField()).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: i18n.t("auth:secondFactorTitle") })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySecondSubmit") })).not.toBeInTheDocument();
  });

  it("keeps the user on the step after a refused passkey, and after a cancel", async () => {
    mockStartSecond.mockResolvedValue(ceremony(9));
    mockComplete.mockRejectedValue(new ApiError(400, "Auth.PasskeyRefused", "That passkey did not work."));
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

  it("tells a passkey-only user that passkeys are off here, and offers only the code to a user with the app", async () => {
    mockStart.mockRejectedValue(new ApiError(404, "Passkeys.Off", "Passkeys are off."));
    mockLogin.mockResolvedValue({ kind: "secondFactor", challenge: "challenge-jwt", expiresAt: "2099-01-01T00:00:00Z", factors: ["passkey"] });
    renderWithProviders(tree(), { route: "/login", token: null });
    await waitFor(() => expect(mockStart).toHaveBeenCalled());
    fireEvent.change(screen.getByLabelText(/Farm code/), { target: { value: "default-farm" } });
    fireEvent.change(emailField(), { target: { value: "owner@farm.co" } });
    fireEvent.change(screen.getByLabelText(/Password/), { target: { value: "pw" } });
    await act(async () => fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:signIn") })));

    expect(screen.getByText(i18n.t("auth:passkeySecondOff"))).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySecondSubmit") })).not.toBeInTheDocument();
  });
});
