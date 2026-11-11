import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { Route, Routes, useLocation, useNavigate } from "react-router";
import { ProtectedRoute } from "./ProtectedRoute";
import { AppLayout } from "./AppLayout";
import { Dashboard } from "./Dashboard";
import { AccountPage } from "./AccountPage";
import { WaterPage } from "./WaterPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { makeToken } from "../test/jwt";
import { freshCode } from "../test/codes";
import { apiGet } from "../api/client";
import i18n from "../i18n";

// #1281 — the Owner's grace deadline, from requiredBy on the status route,
// in the browser's time zone and the user's language. The maintainer chose
// "Dashboard and Account only (Recommended)" (2026-11-10): the banner is on
// those two screens and on no other. The real screens run inside the real
// AppLayout; the network is mocked at fetch, so the request layer and
// AuthContext run too.
const ZONE = "Asia/Kathmandu";
const DAY = 86_400_000;
const ACCOUNT = crypto.randomUUID();
const OWNER = { sub: "u1", role: "Admin", account_id: ACCOUNT, credential_epoch: "1" };
const t = (key: string, options: Record<string, unknown> = {}) => i18n.t(`account:${key}` as "account:heading", options);
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const inZone = (iso: string, language = "en") =>
  new Intl.DateTimeFormat(language, { dateStyle: "medium", timeStyle: "short", timeZone: ZONE }).format(new Date(iso));

let status: Record<string, unknown>;
let requests: string[];
// Requests whose answers the test hands out itself, by key.
let held: Map<string, ((res: Response) => void)[]>;
let holding: Set<string>;
// The next request answers 401 once, and an ordinary refresh follows.
let tokenExpired: boolean;

function answer(key: string): Response {
  if (tokenExpired && key !== "POST /auth/refresh") {
    tokenExpired = false;
    return json({ title: "Unauthorized" }, 401);
  }
  if (key === "POST /auth/refresh") return json({ accessToken: makeToken(OWNER), accessTokenExpiry: "2099-01-01T00:00:00Z" });
  if (key === "GET /auth/two-factor") return json(status);
  if (key.startsWith("GET /reports/production")) return json({ days: [], totalEggs: 0, totalSellable: 0, totalFromCounts: 0 });
  if (key === "POST /auth/step-up") return json({ token: crypto.randomUUID(), expiresAt: "2099-01-01T00:00:00Z" });
  if (key === "POST /auth/two-factor/start") {
    const secret = crypto.randomUUID().replace(/-/g, "").toUpperCase();
    return json({ key: secret, otpauthUri: `otpauth://totp/Cluckwork:owner?secret=${secret}&issuer=Cluckwork` });
  }
  if (key === "POST /auth/two-factor/confirm") {
    status = { enabled: true, recoveryCodesLeft: 10, requiredBy: status.requiredBy };
    return json({
      accessToken: makeToken({ ...OWNER, two_factor: "true" }),
      accessTokenExpiry: "2099-01-01T00:00:00Z",
      recoveryCodes: Array.from({ length: 10 }, () => crypto.randomUUID().slice(0, 19)),
    });
  }
  return json([]);
}

// The browser's history buttons, a link home, a history state this tab did
// not make, and the current history entry, which in-place work keeps.
function History() {
  const navigate = useNavigate();
  return (
    <>
      <output data-testid="entry">{useLocation().key}</output>
      <button type="button" onClick={() => void navigate("/")}>go dashboard</button>
      <button type="button" onClick={() => void navigate(-1)}>back</button>
      <button type="button" onClick={() => void navigate(1)}>forward</button>
      <button type="button" onClick={() => void navigate("/account", { state: { setUpTwoFactor: crypto.randomUUID() } })}>
        stale setup
      </button>
    </>
  );
}

const tree = () => (
  <>
    <History />
    <Routes>
      <Route element={<ProtectedRoute />}>
        <Route element={<AppLayout />}>
          <Route index element={<Dashboard />} />
          <Route path="account" element={<AccountPage />} />
          <Route path="water" element={<WaterPage />} />
        </Route>
      </Route>
    </Routes>
  </>
);
const render = (route = "/account", token: Record<string, unknown> = OWNER) => renderWithProviders(tree(), { route, token });
const [prefix] = t("deadlineBanner", { deadline: "\u0000" }).split("\u0000");
// hidden: a modal dialog marks the page aria-hidden, and the banner is still there.
const banner = () => screen.queryAllByRole("note", { hidden: true }).find((note) => note.textContent?.startsWith(prefix!)) ?? null;
const press = async (name: string, scope: HTMLElement = document.body) => {
  await act(async () => { fireEvent.click(within(scope).getByRole("button", { name })); });
};
const settle = () => act(() => new Promise((resolve) => setTimeout(resolve, 50)));
// The status read has answered, so a banner that is coming is on screen.
const statusRead = async () => {
  await waitFor(() => expect(requests).toContain("GET /auth/two-factor"));
  await settle();
};
const sent = (key: string) => waitFor(() => expect(held.get(key)).toBeDefined());
const answerHeld = (key: string) => act(async () => {
  holding.delete(key);
  for (const resolve of held.get(key)!) resolve(answer(key));
});
const openSetUp = async () => {
  await waitFor(() => expect(banner()).not.toBeNull());
  await press(t("setUpButton"), banner()!);
  return screen.findByRole("dialog", { name: t("setUpTitle") });
};

let zone: string | undefined;
beforeAll(() => {
  zone = process.env.TZ;
  process.env.TZ = ZONE;
});
afterAll(() => {
  process.env.TZ = zone;
});

beforeEach(() => {
  requests = [];
  held = new Map();
  holding = new Set();
  tokenExpired = false;
  status = { enabled: false, recoveryCodesLeft: 0, requiredBy: new Date(Date.now() + 5 * DAY).toISOString() };
  vi.stubGlobal("fetch", vi.fn((url: string, init: RequestInit = {}) => {
    const key = `${init.method ?? "GET"} ${url.replace(/^\/api\/v1/, "")}`;
    requests.push(key);
    if (!holding.has(key)) return Promise.resolve(answer(key));
    return new Promise<Response>((resolve) => { held.set(key, [...(held.get(key) ?? []), resolve]); });
  }));
});

describe("the two-factor deadline banner", () => {
  it.each([["the Dashboard", "/"], ["Account", "/account"]])("shows on %s, with the deadline in the browser's time zone", async (_, route) => {
    render(route);
    await waitFor(() => expect(banner()).toHaveTextContent(t("deadlineBanner", { deadline: inZone(status.requiredBy as string) })));
    expect(inZone(status.requiredBy as string)).not.toBe(
      new Intl.DateTimeFormat("en", { dateStyle: "medium", timeStyle: "short", timeZone: "UTC" }).format(new Date(status.requiredBy as string)));
  });

  it("does not show on a ledger screen", async () => {
    render("/water");
    await screen.findByRole("heading", { name: i18n.t("water:title") });
    await waitFor(() => expect(requests.some((r) => r.startsWith("GET /water"))).toBe(true));
    await settle();
    expect(banner()).toBeNull();
  });

  it("shows the deadline in the user's language", async () => {
    await act(() => i18n.changeLanguage("es"));
    try {
      render();
      await waitFor(() => expect(screen.getAllByRole("note").some((n) => n.textContent?.includes(inZone(status.requiredBy as string, "es")))).toBe(true));
    } finally {
      await act(() => i18n.changeLanguage("en"));
    }
  });

  it("is not shown without requiredBy, so not to a Manager or while the requirement is off", async () => {
    delete status.requiredBy;
    render();
    await statusRead();
    expect(banner()).toBeNull();
  });

  it("is not shown once two-factor is on", async () => {
    status = { ...status, enabled: true, recoveryCodesLeft: 10 };
    render();
    await statusRead();
    expect(banner()).toBeNull();
  });

  // #1274 decision 18: routing reads requiredBy, never the role or the
  // two_factor claim.
  it.each([["the Dashboard", "/"], ["Account", "/account"]])("follows the status route on %s whatever the role and the two_factor claim say", async (_, route) => {
    render(route, { ...OWNER, role: "Manager", two_factor: "true" });
    await waitFor(() => expect(banner()).toHaveTextContent(inZone(status.requiredBy as string)));
  });

  it.each([["the Dashboard", "/"], ["Account", "/account"]])("sends the user from %s to the setup screen once the deadline has passed", async (_, route) => {
    status.requiredBy = new Date(Date.now() - DAY).toISOString();
    render(route);
    expect(await screen.findByRole("heading", { level: 1, name: t("enrolHeading") })).toBeInTheDocument();
  });

  it("opens the setup in place on Account, with no navigation", async () => {
    render("/account");
    await waitFor(() => expect(banner()).not.toBeNull());
    const entry = screen.getByTestId("entry").textContent;
    await openSetUp();
    expect(screen.getByTestId("entry")).toHaveTextContent(entry!);
  });

  it("does not hold up the Dashboard while the status read is out", async () => {
    holding.add("GET /auth/two-factor");
    render("/");
    await sent("GET /auth/two-factor");
    await waitFor(() => expect(screen.queryByText(i18n.t("common:loading"))).not.toBeInTheDocument());
    expect(screen.getByRole("heading", { name: i18n.t("dashboard:title") })).toBeInTheDocument();

    await answerHeld("GET /auth/two-factor");
    await waitFor(() => expect(banner()).not.toBeNull());
  });

  it("opens the setup on Account from the Dashboard, and Back then Forward does not open it again", async () => {
    render("/");
    await press(i18n.t("common:cancel"), await openSetUp());
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());

    await press("back");
    await screen.findByRole("heading", { name: i18n.t("dashboard:title") });
    await press("forward");
    await screen.findByRole("heading", { name: t("twoFactorHeading") });
    await settle();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  // A reload keeps the history entry's state but starts the module afresh.
  it("does not open the setup for a request it did not make", async () => {
    render("/");
    await press("stale setup");
    await screen.findByRole("heading", { name: t("twoFactorHeading") });
    await settle();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("goes from the Dashboard when a confirm sent from Account lands after the user went there", async () => {
    render("/account");
    const proof = await openSetUp();
    fireEvent.change(within(proof).getByLabelText(/Your current password/), { target: { value: `Aa1!${crypto.randomUUID()}` } });
    await press(t("continueButton"), proof);
    await screen.findByRole("heading", { name: t("scanHeading") });
    fireEvent.change(screen.getByLabelText(new RegExp(t("codeLabel"))), { target: { value: freshCode() } });
    holding.add("POST /auth/two-factor/confirm");
    await press(t("confirmButton"));
    await sent("POST /auth/two-factor/confirm");

    await press("go dashboard");
    await screen.findByRole("heading", { name: i18n.t("dashboard:title") });
    await waitFor(() => expect(banner()).not.toBeNull());

    await answerHeld("POST /auth/two-factor/confirm");
    const codes = await screen.findByRole("dialog", { name: t("codesHeading") });
    fireEvent.click(within(codes).getByRole("checkbox"));
    await press(t("doneButton"), codes);
    await waitFor(() => expect(banner()).toBeNull());
  });

  it.each([["the Dashboard", "/"], ["Account", "/account"]])("reads no status again on %s when the token refreshes", async (_, route) => {
    render(route);
    await waitFor(() => expect(banner()).not.toBeNull());
    await settle();
    const reads = requests.filter((r) => r === "GET /auth/two-factor").length;

    tokenExpired = true;
    await act(async () => { await apiGet("/flocks").catch(() => undefined); });
    await settle();

    expect(requests).toContain("POST /auth/refresh");
    expect(requests.filter((r) => r === "GET /auth/two-factor")).toHaveLength(reads);
  });

  it("goes once the user turns two-factor on through Account in the same session", async () => {
    render("/account");
    const proof = await openSetUp();
    fireEvent.change(within(proof).getByLabelText(/Your current password/), { target: { value: `Aa1!${crypto.randomUUID()}` } });
    await press(t("continueButton"), proof);
    await screen.findByRole("heading", { name: t("scanHeading") });
    fireEvent.change(screen.getByLabelText(new RegExp(t("codeLabel"))), { target: { value: freshCode() } });
    await press(t("confirmButton"));

    await screen.findByRole("dialog", { name: t("codesHeading") });
    await waitFor(() => expect(banner()).toBeNull());
  });
});
