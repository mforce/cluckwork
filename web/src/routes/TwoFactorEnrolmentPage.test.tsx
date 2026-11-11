import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { Route, Routes, useLocation, useNavigate } from "react-router";
import { useEffect, useState } from "react";
import { ProtectedRoute } from "./ProtectedRoute";
import { AppLayout } from "./AppLayout";
import { renderWithProviders } from "../test/renderWithProviders";
import { makeToken } from "../test/jwt";
import { freshCode } from "../test/codes";
import { useLatestLoad } from "../components/useLatestLoad";
import { apiGet } from "../api/client";
import { useAuth } from "../auth/useAuth";
import { otherTabSignals } from "../test/otherTab";
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
const FUTURE = new Date(Date.now() + 5 * 86_400_000).toISOString();

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
let homeMounts: number;
// The next request answers 401 once, and the refresh then hands this tab a
// token the gate does not refuse (refreshedClaims).
let tokenExpired: boolean;
let refreshedClaims: Record<string, unknown>;
// Every status read answers 401, as for a session the server has ended, while
// the refresh still answers.
let statusDead: boolean;
let confirmWrong: boolean;
// Answers wait this long, and an aborted request rejects, as in a browser.
let delayMs: number;
// Past this many requests nothing answers, so a request loop fails its test
// instead of running on.
const CEILING = 400;

function answer(key: string): Response {
  if (tokenExpired && key !== "POST /auth/refresh") {
    tokenExpired = false;
    return json({ title: "Unauthorized" }, 401);
  }
  if (key === "GET /auth/two-factor") {
    if (statusDead) return json({ title: "Unauthorized" }, 401);
    if (statusFails) return json({ title: "Server error" }, 500);
    return json({ enabled: enrolled, recoveryCodesLeft: enrolled ? 10 : 0, ...(requiredBy && { requiredBy }) });
  }
  if (key === "POST /auth/step-up") return json({ token: crypto.randomUUID(), expiresAt: "2099-01-01T00:00:00Z" });
  if (key === "POST /auth/two-factor/start") {
    const secret = crypto.randomUUID().replace(/-/g, "").toUpperCase();
    return json({ key: secret, otpauthUri: `otpauth://totp/Cluckwork:owner?secret=${secret}&issuer=Cluckwork` });
  }
  if (key === "POST /auth/two-factor/confirm") {
    if (confirmWrong) return json({ title: "Auth.EnrolmentCodeIncorrect", detail: crypto.randomUUID(), attemptsLeft: 3 }, 400);
    enrolled = true;
    gated = false;
    return pair({ role: "Admin", two_factor: "true" }, { recoveryCodes });
  }
  if (key === "POST /auth/change-password") return pair({ role: "Admin" });
  if (key === "POST /auth/refresh") {
    gated = false;
    requiredBy = undefined;
    return pair(refreshedClaims);
  }
  if (key === "POST /auth/login") return pair({ sub: "u2", role: "Manager" });
  if (key === "POST /auth/logout") return new Response(null, { status: 204 });
  return gated ? refused() : json({});
}

function serve(url: string, init: RequestInit = {}): Promise<Response> {
  const key = `${init.method ?? "GET"} ${url.replace(/^\/api\/v1/, "")}`;
  requests.push(key);
  if (requests.length > CEILING) return new Promise(() => undefined);
  if (holding.has(key)) return new Promise((resolve) => { held.set(key, [...(held.get(key) ?? []), resolve]); });
  if (delayMs === 0) return Promise.resolve(answer(key));
  return new Promise((resolve, reject) => {
    const signal = init.signal ?? undefined;
    if (signal?.aborted) { reject(signal.reason); return; }
    const timer = setTimeout(() => resolve(answer(key)), delayMs);
    signal?.addEventListener("abort", () => { clearTimeout(timer); reject(signal.reason); }, { once: true });
  });
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
  useEffect(() => { homeMounts += 1; }, []);
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
// The same routes inside the app shell, whose strip shows a posted notice.
const shellTree = () => (
  <Routes>
    <Route element={<ProtectedRoute />}>
      <Route element={<AppLayout />}>
        <Route path="/reports" element={<Reports />} />
      </Route>
    </Route>
  </Routes>
);
const OWNER = { sub: "u1", role: "Admin", account_id: ACCOUNT, credential_epoch: "1" };
const render = (route: string, token: Record<string, unknown> = OWNER) => renderWithProviders(tree(), { route, token, me: null });

const setUpScreen = () => screen.findByRole("button", { name: t("setUpButton") });
const setUpShown = () => screen.queryByRole("heading", { name: t("enrolHeading") }) !== null;
const press = async (name: string, scope: HTMLElement = document.body) => {
  await act(async () => { fireEvent.click(within(scope).getByRole("button", { name })); });
};
const settle = () => act(() => new Promise((resolve) => setTimeout(resolve, 100)));
// In short act scopes, so each answer is rendered, and its effects run,
// before the next request answers, as in a browser.
const wait = async (ms: number) => {
  const end = Date.now() + ms;
  while (Date.now() < end) await act(() => new Promise((resolve) => setTimeout(resolve, 2)));
};
const count = (key: string) => requests.filter((r) => r === key).length;
// A screen sends its request after its effects run, which can be after the
// render a test waits for, so a test that answers a held request waits for it.
const sent = (key: string) => waitFor(() => expect(held.get(key)).toBeDefined());

async function toScanStep() {
  await press(t("setUpButton"), (await setUpScreen()).closest("main")!);
  const proof = screen.getByRole("dialog");
  fireEvent.change(within(proof).getByLabelText(/Your current password/), { target: { value: `Aa1!${crypto.randomUUID()}` } });
  await press(t("continueButton"), proof);
  await screen.findByRole("heading", { name: t("scanHeading") });
}

async function enrol() {
  await toScanStep();
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
  homeMounts = 0;
  tokenExpired = false;
  // An ordinary refresh: the same user, with the same credentials.
  refreshedClaims = { role: "Admin", credential_epoch: "1" };
  statusDead = false;
  confirmWrong = false;
  delayMs = 0;
  recoveryCodes = Array.from({ length: 10 }, () => crypto.randomUUID().slice(0, 19));
  vi.stubGlobal("fetch", vi.fn((url: string, init?: RequestInit) => serve(url, init)));
  // An effect that sets state on every render spins inside act and would hang
  // the file. React reports it with this message, so a loop fails its test.
  const report = console.error;
  vi.spyOn(console, "error").mockImplementation((...args: unknown[]) => {
    if (String(args[0]).includes("Maximum update depth")) throw new Error("render loop");
    report(...args);
  });
});

afterEach(() => {
  vi.restoreAllMocks();
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

  // #1398 — the status read after the confirm is held, so the codes show over
  // this screen; the other tab's signal takes them, and nothing else.
  it("drops the unsaved codes on another tab's sign-in or sign-out, lets the user go, and the shell says why", async () => {
    renderWithProviders(shellTree(), { route: "/reports", token: OWNER, me: null });
    holding.add("GET /auth/two-factor");
    await enrol();
    await screen.findByText(recoveryCodes[0]!);
    expect(screen.getByRole("heading", { name: t("enrolHeading"), hidden: true })).toBeInTheDocument();

    await otherTabSignals();

    for (const code of recoveryCodes) expect(document.body.innerHTML).not.toContain(code);
    expect(setUpShown()).toBe(true);
    expect(requests).not.toContain("POST /auth/logout");

    holding.delete("GET /auth/two-factor");
    await act(async () => { for (const resolve of held.get("GET /auth/two-factor") ?? []) resolve(answer("GET /auth/two-factor")); });
    expect(await screen.findByText(/reports page at \/reports loaded/)).toBeInTheDocument();
    expect(document.querySelector(".shell-notice")).toHaveTextContent(t("codesDroppedNotice"));
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
    await sent("GET /auth/two-factor");

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

  // A next user inside the grace period would be held: a requiredBy in the
  // future keeps the setup screen.
  it.each([
    ["with no deadline", undefined],
    ["inside the grace period", FUTURE],
  ])("ignores a 403 from an earlier session that lands on the next user %s, and leaves their screen mounted", async (_, next) => {
    gated = false;
    render("/");
    holding.add("GET /flocks");
    await press("load flocks");
    await sent("GET /flocks");
    holding.clear();
    await press("sign out of home");
    await screen.findByText(/login screen/);
    requiredBy = next;
    await press("sign in");
    await screen.findByText(/protected home/);
    const mounts = homeMounts;

    await act(async () => { held.get("GET /flocks")![0]!(refused()); });
    await settle();

    expect(setUpShown()).toBe(false);
    expect(screen.getByText(/protected home/)).toBeInTheDocument();
    expect(homeMounts).toBe(mounts);
  });

  // A refresh can hand this tab a token the gate does not refuse with no
  // sign-in here: another user's, from a second tab on this farm, or this
  // user's after another Owner made them a Manager.
  // A role change bumps the credential epoch (#1274 decision 18).
  it.each([
    ["another user's", { sub: "u2", role: "Manager", credential_epoch: "1" }],
    ["this user's as a Manager", { sub: "u1", role: "Manager", credential_epoch: "2" }],
  ])("reads the status again when a refresh adopts %s token, and lets the user go", async (_, claims) => {
    render("/reports");
    await setUpScreen();
    await waitFor(() => expect(requests).toContain("GET /auth/two-factor"));
    await settle();

    refreshedClaims = claims;
    tokenExpired = true;
    await act(async () => { await apiGet("/flocks").catch(() => undefined); });

    expect(requests).toContain("POST /auth/refresh");
    expect(await screen.findByText(/reports page at \/reports loaded/)).toBeInTheDocument();
  });

  // At 304ae233 the refresh made the status read again and aborted the
  // resend whose second 401 ends the session: 67 status reads in 1.5 s.
  it("ends the session when the status read answers 401 to a fresh token, with a bounded number of requests", async () => {
    delayMs = 20;
    statusDead = true;
    render("/reports");
    await setUpScreen();
    await wait(1500);

    expect(count("POST /auth/refresh")).toBeLessThanOrEqual(1);
    expect(count("GET /auth/two-factor")).toBeLessThanOrEqual(2);
    expect(screen.getByText("login screen")).toBeInTheDocument();
  }, 10_000);

  it("keeps the scan step, its key and the typed code across an ordinary refresh, and reads nothing again", async () => {
    render("/reports");
    await toScanStep();
    const key = document.querySelector("code")!.textContent;
    const typed = freshCode();
    fireEvent.change(screen.getByLabelText(new RegExp(t("codeLabel"))), { target: { value: typed } });
    const reads = count("GET /auth/two-factor");

    tokenExpired = true;
    await act(async () => { await apiGet("/flocks").catch(() => undefined); });
    await settle();

    expect(count("POST /auth/refresh")).toBe(1);
    expect(count("GET /auth/two-factor")).toBe(reads);
    expect(document.querySelector("code")!.textContent).toBe(key);
    expect(screen.getByLabelText(new RegExp(t("codeLabel")))).toHaveValue(typed);
  });

  it("reads the status again on Try again, and can let the user go", async () => {
    statusFails = true;
    render("/reports");
    await screen.findByText(t("statusLoadFailed"));

    statusFails = false;
    gated = false;
    requiredBy = undefined;
    await press(t("statusRetry"));

    expect(await screen.findByText(/reports page at \/reports loaded/)).toBeInTheDocument();
  });

  // A sign-out then would drop the answer that carries the only copy of the
  // recovery codes.
  it("keeps Sign out disabled while the confirm is in flight", async () => {
    render("/reports");
    await toScanStep();
    fireEvent.change(screen.getByLabelText(new RegExp(t("codeLabel"))), { target: { value: freshCode() } });
    holding.add("POST /auth/two-factor/confirm");
    await press(t("confirmButton"));
    await sent("POST /auth/two-factor/confirm");

    expect(screen.getByRole("button", { name: t("requiredSignOut") })).toBeDisabled();
    holding.clear();
    await act(async () => { held.get("POST /auth/two-factor/confirm")![0]!(answer("POST /auth/two-factor/confirm")); });
    await settle();
  });

  it("moves focus to the scan step's heading when the proof dialog closes into it", async () => {
    render("/reports");
    await toScanStep();
    await waitFor(() => expect(screen.getByRole("heading", { level: 2, name: t("enrolHeading") })).toHaveFocus());
  });

  it("leaves focus in the code field while the user types, and after a wrong code", async () => {
    render("/reports");
    await toScanStep();
    expect(screen.getByRole("heading", { level: 2, name: t("enrolHeading") })).toHaveAttribute("tabindex", "-1");
    const field = screen.getByLabelText(new RegExp(t("codeLabel")));
    act(() => field.focus());
    fireEvent.change(field, { target: { value: "1" } });
    fireEvent.change(field, { target: { value: "12" } });
    await settle();
    expect(field).toHaveFocus();

    confirmWrong = true;
    fireEvent.change(field, { target: { value: freshCode() } });
    await press(t("confirmButton"));
    await screen.findByText(new RegExp(t("codeIncorrectAttempts_other").split("{{")[0]!));
    expect(screen.getByRole("heading", { level: 2, name: t("enrolHeading") })).not.toHaveFocus();
  });

  it("leaves focus in the proof dialog after a Cancel, while the user types", async () => {
    render("/reports");
    await toScanStep();
    await press(i18n.t("common:cancel"));
    await waitFor(() => expect(screen.getByRole("button", { name: t("setUpButton") })).toHaveFocus());

    await press(t("setUpButton"));
    const password = within(await screen.findByRole("dialog")).getByLabelText(/Your current password/);
    act(() => password.focus());
    fireEvent.change(password, { target: { value: "x" } });
    await settle();
    expect(password).toHaveFocus();
  });

  it("moves focus back to Set up when the user cancels the scan step", async () => {
    render("/reports");
    await toScanStep();
    await press(i18n.t("common:cancel"));
    await waitFor(() => expect(screen.getByRole("button", { name: t("setUpButton") })).toHaveFocus());
  });

  it("does not hold the user when a 403 sent before the confirm lands after it", async () => {
    gated = false;
    render("/");
    holding.add("GET /flocks");
    await press("load flocks");
    await sent("GET /flocks");
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
    await sent("POST /auth/logout");
    expect(signOut).toBeDisabled();
    // jsdom does not model inert, so the attribute is what can be checked.
    if (holdsSetUp) expect(screen.getByRole("button", { name: t("setUpButton") }).closest("[inert]")).not.toBeNull();
    await act(async () => { fireEvent.click(signOut); });
    await settle();

    expect(requests.filter((r) => r === "POST /auth/logout")).toHaveLength(1);
    holding.clear();
    await act(async () => { held.get("POST /auth/logout")![0]!(new Response(null, { status: 204 })); });
    expect(await screen.findByText("login screen")).toBeInTheDocument();
  });
});
