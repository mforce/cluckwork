import { beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { Route, Routes, useLocation, useNavigate } from "react-router";
import { useState } from "react";
import { ProtectedRoute } from "./ProtectedRoute";
import { renderWithProviders } from "../test/renderWithProviders";
import { makeToken } from "../test/jwt";
import { freshCode } from "../test/codes";
import { useLatestLoad } from "../components/useLatestLoad";
import { apiGet } from "../api/client";
import { useAuth } from "../auth/useAuth";
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
let requiredBy: string | undefined;
let statusFails: boolean;
let requests: string[];
let recoveryCodes: string[];
// Requests whose answers the test hands out itself, by key.
let held: Map<string, ((res: Response) => void)[]>;
let holding: Set<string>;

function answer(key: string): Response {
  if (key === "GET /auth/two-factor") {
    if (statusFails) return json({ title: "Server error" }, 500);
    return json({ enabled: enrolled, recoveryCodesLeft: enrolled ? 10 : 0, ...(requiredBy && { requiredBy }) });
  }
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
  if (key === "POST /auth/login") return pair({ sub: "u2", role: "Manager" });
  if (key === "POST /auth/logout") return new Response(null, { status: 204 });
  return gated ? refused() : json({});
}

function serve(url: string, init: RequestInit = {}): Promise<Response> {
  const key = `${init.method ?? "GET"} ${url.replace(/^\/api\/v1/, "")}`;
  requests.push(key);
  if (!holding.has(key)) return Promise.resolve(answer(key));
  return new Promise((resolve) => { held.set(key, [...(held.get(key) ?? []), resolve]); });
}

// A screen behind the gate that loads on mount, as every app screen does.
function Reports() {
  const { pathname } = useLocation();
  const load = useLatestLoad([], (signal) => apiGet("/reports/summary", signal));
  return <div>reports page at {pathname}{load.data !== null && " loaded"}</div>;
}

// A screen that is fine until the user acts after the deadline passed.
function Home() {
  const { logout } = useAuth();
  const [saved, setSaved] = useState(false);
  return (
    <div>
      protected home
      <button type="button" onClick={() => void apiGet("/flocks").then(() => setSaved(true), () => undefined)}>load flocks</button>
      <button type="button" onClick={() => void logout()}>sign out of home</button>
      {saved && " saved"}
    </div>
  );
}

function Login() {
  const { login } = useAuth();
  const navigate = useNavigate();
  return (
    <div>
      login screen
      <button type="button" onClick={() => void login("default-farm", "m@farm.local", crypto.randomUUID()).then(() => navigate("/"))}>sign in</button>
    </div>
  );
}

const tree = () => (
  <Routes>
    <Route path="/login" element={<Login />} />
    <Route element={<ProtectedRoute />}>
      <Route path="/" element={<Home />} />
      <Route path="/reports" element={<Reports />} />
    </Route>
  </Routes>
);
const OWNER = { sub: "u1", role: "Admin", account_id: ACCOUNT };
const render = (route: string, token: Record<string, unknown> = OWNER) => renderWithProviders(tree(), { route, token, me: null });

const setUpScreen = () => screen.findByRole("button", { name: t("setUpButton") });
const setUpShown = () => screen.queryByRole("heading", { name: t("enrolHeading") }) !== null;
const press = async (name: string, scope: HTMLElement = document.body) => {
  await act(async () => { fireEvent.click(within(scope).getByRole("button", { name })); });
};
const settle = () => act(() => new Promise((resolve) => setTimeout(resolve, 100)));

async function enrol() {
  await press(t("setUpButton"), (await setUpScreen()).closest("main")!);
  const proof = screen.getByRole("dialog");
  fireEvent.change(within(proof).getByLabelText(/Your current password/), { target: { value: `Aa1!${crypto.randomUUID()}` } });
  await press(t("continueButton"), proof);
  await screen.findByRole("heading", { name: t("scanHeading") });
  fireEvent.change(screen.getByLabelText(new RegExp(t("codeLabel"))), { target: { value: freshCode() } });
  await press(t("confirmButton"));
}

beforeEach(() => {
  gated = true;
  enrolled = false;
  requiredBy = PAST;
  statusFails = false;
  requests = [];
  held = new Map();
  holding = new Set();
  recoveryCodes = Array.from({ length: 10 }, () => crypto.randomUUID().slice(0, 19));
  vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => serve(url, init)));
});

describe("the forced two-factor setup screen", () => {
  it("replaces the screen a 403 came from, focuses its heading, and fires only requests the gate allows", async () => {
    render("/reports");

    await setUpScreen();
    expect(screen.queryByText(/reports page/)).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole("heading", { level: 1, name: t("enrolHeading") })).toHaveFocus());
    expect(document.title).toContain(t("enrolHeading"));
    await waitFor(() => expect(requests).toContain("GET /auth/two-factor"));
    await settle();
    const first = requests.indexOf("GET /reports/summary");
    expect(requests.slice(first + 1).filter((r) => !ALLOWED.has(r))).toEqual([]);
  });

  it("enrols through step-up, the QR code and the confirm code, then shows the codes and the page asked for", async () => {
    render("/reports");
    await enrol();

    expect(await screen.findByText(recoveryCodes[0]!)).toBeInTheDocument();
    expect(await screen.findByText(/reports page at \/reports loaded/)).toBeInTheDocument();
    expect(requests.filter((r) => !ALLOWED.has(r))).toEqual(["GET /reports/summary", "GET /reports/summary"]);
  });

  it("follows a 403 that arrives mid-session, after the screen had loaded", async () => {
    gated = false;
    render("/");
    await press("load flocks");
    await screen.findByText(/saved/);

    gated = true;
    await press("load flocks");
    await setUpScreen();
    expect(screen.queryByText(/protected home/)).not.toBeInTheDocument();
  });

  it("is not shown without the 403, for an Owner without two-factor", async () => {
    gated = false;
    render("/reports");
    expect(await screen.findByText(/reports page at \/reports loaded/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: t("setUpButton") })).not.toBeInTheDocument();
  });

  // #1274 decision 18: the SPA routes on the 403 and on requiredBy, never on
  // the role and the two_factor claim, which do not follow
  // TwoFactor:RequireForOwners.
  it("follows the 403 for any role and whatever the two_factor claim says", async () => {
    render("/reports", { sub: "u1", role: "Manager", two_factor: "true", account_id: ACCOUNT });
    await setUpScreen();
    expect(screen.queryByText(/reports page/)).not.toBeInTheDocument();
  });

  it("comes after the forced password change, with both required at once", async () => {
    render("/reports", { ...OWNER, must_change_password: "true" });
    expect(screen.getByRole("heading", { name: i18n.t("auth:setPasswordHeading") })).toBeInTheDocument();
    expect(requests).toEqual([]);

    // The gate refuses a request while the password screen is up.
    await act(async () => { await apiGet("/flocks").catch(() => undefined); });
    expect(screen.getByRole("heading", { name: i18n.t("auth:setPasswordHeading") })).toBeInTheDocument();
    expect(setUpShown()).toBe(false);

    const next = `Aa1!${crypto.randomUUID()}`;
    fireEvent.change(screen.getByLabelText(new RegExp(i18n.t("auth:temporaryPasswordLabel"))), { target: { value: crypto.randomUUID() } });
    fireEvent.change(screen.getByLabelText(/New password/), { target: { value: next } });
    fireEvent.change(screen.getByLabelText(new RegExp(i18n.t("auth:setPasswordConfirmLabel"))), { target: { value: next } });
    await press(i18n.t("auth:setPasswordButton"));

    await setUpScreen();
    expect(screen.queryByRole("heading", { name: i18n.t("auth:setPasswordHeading") })).not.toBeInTheDocument();
  });

  it("releases the user when the status route sends no requiredBy, as when the requirement is switched off", async () => {
    holding.add("GET /auth/two-factor");
    render("/reports");
    await setUpScreen();

    gated = false;
    requiredBy = undefined;
    holding.clear();
    await act(async () => { for (const resolve of held.get("GET /auth/two-factor")!) resolve(answer("GET /auth/two-factor")); });

    expect(await screen.findByText(/reports page at \/reports loaded/)).toBeInTheDocument();
    expect(setUpShown()).toBe(false);
  });

  it("keeps the user there when the status read fails", async () => {
    statusFails = true;
    render("/reports");
    expect(await screen.findByText(t("statusLoadFailed"))).toBeInTheDocument();
    await settle();
    expect(setUpShown()).toBe(true);
  });

  // The gate goes by the server's clock, so a browser running behind must not
  // send the user back to a page the gate refuses.
  it("keeps the user there when the status route puts the deadline in the future", async () => {
    requiredBy = new Date(Date.now() + 86_400_000).toISOString();
    render("/reports");
    await setUpScreen();
    await waitFor(() => expect(requests).toContain("GET /auth/two-factor"));
    await settle();

    expect(setUpShown()).toBe(true);
    expect(requests.filter((r) => r === "GET /reports/summary")).toHaveLength(1);
  });

  it("ignores a 403 from an earlier session that lands on the next user", async () => {
    gated = false;
    render("/");
    holding.add("GET /flocks");
    await press("load flocks");
    holding.clear();
    await press("sign out of home");
    await screen.findByText(/login screen/);
    requiredBy = undefined;
    await press("sign in");
    await screen.findByText(/protected home/);

    await act(async () => { held.get("GET /flocks")![0]!(refused()); });
    await settle();

    expect(setUpShown()).toBe(false);
    expect(screen.getByText(/protected home/)).toBeInTheDocument();
  });

  it("does not hold the user when a 403 sent before the confirm lands after it", async () => {
    gated = false;
    render("/");
    holding.add("GET /flocks");
    await press("load flocks");
    holding.clear();
    gated = true;
    await press("load flocks");
    await enrol();
    await screen.findByText(/protected home/);

    await act(async () => { held.get("GET /flocks")![0]!(refused()); });
    await settle();

    expect(setUpShown()).toBe(false);
    expect(screen.getByText(/protected home/)).toBeInTheDocument();
  });

  it("offers sign-out", async () => {
    render("/reports");
    await setUpScreen();
    await press(t("requiredSignOut"));
    expect(await screen.findByText("login screen")).toBeInTheDocument();
    expect(requests).toContain("POST /auth/logout");
  });

  it.each([
    ["the setup screen", OWNER, t("requiredSignOut"), true],
    ["the password screen", { ...OWNER, must_change_password: "true" }, i18n.t("auth:setPasswordSignOut"), false],
  ])("signs out once when Sign out on %s is pressed twice", async (_, token, label, holdsSetUp) => {
    render("/reports", token);
    const signOut = await screen.findByRole("button", { name: label });
    holding.add("POST /auth/logout");

    await act(async () => { fireEvent.click(signOut); });
    expect(signOut).toBeDisabled();
    // jsdom does not model inert, so the attribute is what can be checked.
    if (holdsSetUp) expect(screen.getByRole("button", { name: t("setUpButton") }).closest("[inert]")).not.toBeNull();
    await act(async () => { fireEvent.click(signOut); });

    expect(requests.filter((r) => r === "POST /auth/logout")).toHaveLength(1);
    holding.clear();
    await act(async () => { held.get("POST /auth/logout")![0]!(new Response(null, { status: 204 })); });
    expect(await screen.findByText("login screen")).toBeInTheDocument();
  });
});
