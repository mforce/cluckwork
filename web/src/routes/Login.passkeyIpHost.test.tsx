// @vitest-environment-options {"url": "http://127.0.0.1/login"}
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { screen, fireEvent, act } from "@testing-library/react";
import { Routes, Route } from "react-router";
import { Login } from "./Login";
import { renderWithProviders } from "../test/renderWithProviders";
import { login as apiLogin, startPasskeySignIn } from "../api/client";
import type { SecondFactor } from "../api/client";
import { previewConsent } from "../api/oauth";
import i18n from "../i18n";

// #1285 — WebAuthn refuses an IP address as the relying party, and the server
// runs no ceremony there (record 1282 decision 5), as on the e2e stack's
// http://127.0.0.1 address. This file runs at such a host.
vi.mock("../api/client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/client")>();
  return {
    ...actual,
    login: vi.fn(),
    startPasskeySignIn: vi.fn(),
    logout: vi.fn(),
    setOnTokensChanged: vi.fn(),
    setOnUnauthenticated: vi.fn(),
  };
});
vi.mock("../api/oauth", () => ({ previewConsent: vi.fn() }));

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(previewConsent).mockResolvedValue(null);
  vi.stubGlobal("PublicKeyCredential", {
    parseRequestOptionsFromJSON: (options: unknown) => options,
    parseCreationOptionsFromJSON: (options: unknown) => options,
    isConditionalMediationAvailable: async () => true,
  });
});

afterEach(() => vi.unstubAllGlobals());

async function render() {
  renderWithProviders(<Routes><Route path="/login" element={<Login />} /></Routes>, { route: "/login", token: null });
  await screen.findByRole("button", { name: i18n.t("auth:signIn") });
  await act(async () => {});
}

async function passwordStep(factors: SecondFactor[]) {
  vi.mocked(apiLogin).mockResolvedValue({ kind: "secondFactor", challenge: "challenge-jwt", expiresAt: "2099-01-01T00:00:00Z", factors });
  fireEvent.change(screen.getByLabelText(/Farm code/), { target: { value: "default-farm" } });
  fireEvent.change(screen.getByLabelText(/Email/), { target: { value: "owner@farm.co" } });
  fireEvent.change(screen.getByLabelText(/Password/), { target: { value: "pw" } });
  await act(async () => fireEvent.click(screen.getByRole("button", { name: i18n.t("auth:signIn") })));
}

describe("Login with passkeys on an IP address (#1285)", () => {
  it("asks for no ceremony and offers no passkey", async () => {
    expect(window.location.hostname).toBe("127.0.0.1");
    await render();

    expect(startPasskeySignIn).not.toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySignIn") })).not.toBeInTheDocument();
  });

  it("starts a user with both factors on the code, with no passkey to switch to", async () => {
    await render();
    await passwordStep(["authenticator", "passkey"]);

    expect(screen.getByLabelText(new RegExp(`^${i18n.t("auth:secondFactorCode")}`))).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: i18n.t("auth:passkeySecondSubmit") })).not.toBeInTheDocument();
  });

  it("tells a passkey-only user that passkeys are off here", async () => {
    await render();
    await passwordStep(["passkey"]);

    expect(screen.getByText(i18n.t("auth:passkeySecondOff"))).toBeInTheDocument();
  });
});
