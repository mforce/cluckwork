import { beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { Route, Routes, useLocation } from "react-router";
import { useState } from "react";
import { ProtectedRoute } from "./ProtectedRoute";
import { renderWithProviders } from "../test/renderWithProviders";
import { makeToken } from "../test/jwt";
import { freshCode } from "../test/codes";
import { useLatestLoad } from "../components/useLatestLoad";
import { apiGet } from "../api/client";
import i18n from "../i18n";

// #1281 — an Owner past the grace deadline. The API answers 403
// Auth.TwoFactorEnrolmentRequired to everything but the steps of enrolment,
// so the app shows the setup screen and nothing else. The network is mocked
// at fetch, so the real request layer, AuthContext and ProtectedRoute run.
const ENROLMENT_REQUIRED = "Auth.TwoFactorEnrolmentRequired";
// What the gate lets through (OwnerTwoFactorGateMiddleware), plus logout.
const ALLOWED = new Set([
  "POST /auth/change-password", "POST /auth/step-up", "GET /auth/two-factor",
  "POST /auth/two-factor/start", "POST /auth/two-factor/confirm", "POST /auth/logout",
]);
const ACCOUNT = crypto.randomUUID();
const PAST = new Date(Date.now() - 86_400_000).toISOString();

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const refused = () => json({ title: ENROLMENT_REQUIRED, detail: "Set up two-factor authentication." }, 403);
const pair = (claims: Record<string, unknown>, extra: object = {}) =>
  json({ accessToken: makeToken({ sub: "u1", account_id: ACCOUNT, ...claims }), accessTokenExpiry: "2099-01-01T00:00:00Z", ...extra });
const t = (key: string) => i18n.t(`account:${key}` as "account:heading");

let gated: boolean;
let enrolled: boolean;
let requests: string[];
let recoveryCodes: string[];

function serve(url: string, init: RequestInit = {}): Response {
  const method = init.method ?? "GET";
  const path = url.replace(/^\/api\/v1/, "");
  const key = `${method} ${path}`;
  requests.push(key);
  if (key === "GET /auth/two-factor") return json({ enabled: enrolled, recoveryCodesLeft: enrolled ? 10 : 0, requiredBy: PAST });
  if (key === "POST /auth/step-up") return json({ token: crypto.randomUUID(), expiresAt: "2099-01-01T00:00:00Z" });
  if (key === "POST /auth/two-factor/start") {
    const secret = crypto.randomUUID().replace(/-/g, "").toUpperCase();
    return json({ key: secret, otpauthUri: `otpauth://totp/Cluckwork:owner?secret=${secret}&issuer=Cluckwork` });
  }
  if (key === "POST /auth/two-factor/confirm") {
    enrolled = true;
    gated = false;
    return pair({ role: "Admin", two_factor: "true" }, { recoveryCodes });
  }
  if (key === "POST /auth/change-password") return pair({ role: "Admin" });
  if (key === "POST /auth/logout") return new Response(null, { status: 204 });
  return gated ? refused() : json({});
}

// A screen behind the gate that loads on mount, as every app screen does.
function Reports() {
  const { pathname } = useLocation();
  const load = useLatestLoad([], (signal) => apiGet("/reports/summary", signal));
  return <div>reports page at {pathname}{load.data !== null && " loaded"}</div>;
}

// A screen that is fine until the user acts after the deadline passed.
function Home() {
  const [saved, setSaved] = useState(false);
  return (
    <div>
      protected home
      <button type="button" onClick={() => void apiGet("/flocks").then(() => setSaved(true), () => undefined)}>load flocks</button>
      {saved && " saved"}
    </div>
  );
}

const tree = () => (
  <Routes>
    <Route path="/login" element={<div>login screen</div>} />
    <Route element={<ProtectedRoute />}>
      <Route path="/" element={<Home />} />
      <Route path="/reports" element={<Reports />} />
    </Route>
  </Routes>
);

const setUpScreen = () => screen.findByRole("button", { name: t("setUpButton") });
const press = async (name: string, scope: HTMLElement = document.body) => {
  await act(async () => { fireEvent.click(within(scope).getByRole("button", { name })); });
};

beforeEach(() => {
  gated = true;
  enrolled = false;
  requests = [];
  recoveryCodes = Array.from({ length: 10 }, () => crypto.randomUUID().slice(0, 19));
  vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => Promise.resolve(serve(url, init))));
});

describe("the forced two-factor setup screen", () => {
  it("replaces the screen a 403 came from, and fires only requests the gate allows", async () => {
    renderWithProviders(tree(), { route: "/reports", token: { sub: "u1", role: "Admin", account_id: ACCOUNT }, me: null });

    await setUpScreen();
    expect(screen.queryByText(/reports page/)).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { name: t("enrolHeading") })).toBeInTheDocument();
    const sinceShown = requests.length;
    await waitFor(() => expect(requests).toContain("GET /auth/two-factor"));
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(requests.slice(sinceShown).filter((r) => !ALLOWED.has(r))).toEqual([]);
  });

  it("enrols through step-up, the QR code and the confirm code, then shows the codes and the page asked for", async () => {
    renderWithProviders(tree(), { route: "/reports", token: { sub: "u1", role: "Admin", account_id: ACCOUNT }, me: null });

    await press(t("setUpButton"), (await setUpScreen()).closest("main")!);
    const proof = screen.getByRole("dialog");
    fireEvent.change(within(proof).getByLabelText(/Your current password/), { target: { value: `Aa1!${crypto.randomUUID()}` } });
    await press(t("continueButton"), proof);
    await screen.findByRole("heading", { name: t("scanHeading") });
    fireEvent.change(screen.getByLabelText(new RegExp(t("codeLabel"))), { target: { value: freshCode() } });
    await press(t("confirmButton"));

    expect(await screen.findByText(recoveryCodes[0]!)).toBeInTheDocument();
    expect(await screen.findByText(/reports page at \/reports loaded/)).toBeInTheDocument();
    expect(requests.filter((r) => !ALLOWED.has(r))).toEqual(["GET /reports/summary", "GET /reports/summary"]);
  });

  it("follows a 403 that arrives mid-session, after the screen had loaded", async () => {
    gated = false;
    renderWithProviders(tree(), { route: "/", token: { sub: "u1", role: "Admin", account_id: ACCOUNT }, me: null });
    await press("load flocks");
    await screen.findByText(/saved/);

    gated = true;
    await press("load flocks");
    await setUpScreen();
    expect(screen.queryByText(/protected home/)).not.toBeInTheDocument();
  });

  it("is not shown without the 403, for an Owner without two-factor", async () => {
    gated = false;
    renderWithProviders(tree(), { route: "/reports", token: { sub: "u1", role: "Admin", account_id: ACCOUNT }, me: null });
    expect(await screen.findByText(/reports page at \/reports loaded/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: t("setUpButton") })).not.toBeInTheDocument();
  });

  // #1274 decision 18: the SPA routes on the 403 and on requiredBy, never on
  // the role and the two_factor claim, which do not follow
  // TwoFactor:RequireForOwners.
  it("follows the 403 for any role and whatever the two_factor claim says", async () => {
    renderWithProviders(tree(), { route: "/reports", token: { sub: "u1", role: "Manager", two_factor: "true", account_id: ACCOUNT }, me: null });
    await setUpScreen();
    expect(screen.queryByText(/reports page/)).not.toBeInTheDocument();
  });

  it("comes after the forced password change", async () => {
    renderWithProviders(tree(), {
      route: "/reports", token: { sub: "u1", role: "Admin", must_change_password: "true", account_id: ACCOUNT }, me: null,
    });
    expect(screen.getByRole("heading", { name: i18n.t("auth:setPasswordHeading") })).toBeInTheDocument();
    expect(requests).toEqual([]);

    const next = `Aa1!${crypto.randomUUID()}`;
    fireEvent.change(screen.getByLabelText(new RegExp(i18n.t("auth:temporaryPasswordLabel"))), { target: { value: crypto.randomUUID() } });
    fireEvent.change(screen.getByLabelText(/New password/), { target: { value: next } });
    fireEvent.change(screen.getByLabelText(new RegExp(i18n.t("auth:setPasswordConfirmLabel"))), { target: { value: next } });
    await press(i18n.t("auth:setPasswordButton"));

    await setUpScreen();
    expect(screen.queryByRole("heading", { name: i18n.t("auth:setPasswordHeading") })).not.toBeInTheDocument();
  });

  it("offers sign-out", async () => {
    renderWithProviders(tree(), { route: "/reports", token: { sub: "u1", role: "Admin", account_id: ACCOUNT }, me: null });
    await setUpScreen();
    await press(t("requiredSignOut"));
    expect(await screen.findByText("login screen")).toBeInTheDocument();
    expect(requests).toContain("POST /auth/logout");
  });
});
