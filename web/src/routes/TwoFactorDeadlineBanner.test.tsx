import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { Outlet, Route, Routes, useNavigate } from "react-router";
import { ProtectedRoute } from "./ProtectedRoute";
import { TwoFactorDeadlineBanner } from "./TwoFactorDeadlineBanner";
import { AccountPage } from "./AccountPage";
import { renderWithProviders } from "../test/renderWithProviders";
import { makeToken } from "../test/jwt";
import { freshCode } from "../test/codes";
import i18n from "../i18n";

// #1281 — the Owner's grace deadline, from requiredBy on the status route,
// in the browser's time zone and the user's language. The network is mocked
// at fetch, so the real request layer and AuthContext run.
const ZONE = "Asia/Kathmandu";
const DAY = 86_400_000;
const t = (key: string, options: Record<string, unknown> = {}) => i18n.t(`account:${key}` as "account:heading", options);
const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const inZone = (iso: string, language = "en") =>
  new Intl.DateTimeFormat(language, { dateStyle: "medium", timeStyle: "short", timeZone: ZONE }).format(new Date(iso));

const ACCOUNT = crypto.randomUUID();

let status: Record<string, unknown>;
let requests: string[];

function answer(key: string): Response {
  if (key === "GET /auth/two-factor") return json(status);
  if (key === "POST /auth/step-up") return json({ token: crypto.randomUUID(), expiresAt: "2099-01-01T00:00:00Z" });
  if (key === "POST /auth/two-factor/start") {
    const secret = crypto.randomUUID().replace(/-/g, "").toUpperCase();
    return json({ key: secret, otpauthUri: `otpauth://totp/Cluckwork:owner?secret=${secret}&issuer=Cluckwork` });
  }
  if (key === "POST /auth/two-factor/confirm") {
    status = { enabled: true, recoveryCodesLeft: 10, requiredBy: status.requiredBy };
    return json({
      accessToken: makeToken({ sub: "u1", role: "Admin", two_factor: "true", account_id: ACCOUNT }),
      accessTokenExpiry: "2099-01-01T00:00:00Z",
      recoveryCodes: Array.from({ length: 10 }, () => crypto.randomUUID().slice(0, 19)),
    });
  }
  return json([]);
}

// The shell's place for the banner, above every screen, and the browser's
// history buttons.
function Shell() {
  const navigate = useNavigate();
  return (
    <>
      <TwoFactorDeadlineBanner />
      <button type="button" onClick={() => void navigate(-1)}>back</button>
      <button type="button" onClick={() => void navigate(1)}>forward</button>
      <button type="button" onClick={() => void navigate("/account", { state: { setUpTwoFactor: crypto.randomUUID() } })}>
        stale setup
      </button>
      <Outlet />
    </>
  );
}

const tree = () => (
  <Routes>
    <Route element={<ProtectedRoute />}>
      <Route element={<Shell />}>
        <Route path="/" element={<div>protected home</div>} />
        <Route path="/account" element={<AccountPage />} />
      </Route>
    </Route>
  </Routes>
);
const render = (token: Record<string, unknown> = { sub: "u1", role: "Admin", account_id: ACCOUNT }) =>
  renderWithProviders(tree(), { route: "/", token });
const [prefix] = t("deadlineBanner", { deadline: "\u0000" }).split("\u0000");
// hidden: a modal dialog marks the page aria-hidden, and the banner is still there.
const banner = () => screen.queryAllByRole("note", { hidden: true }).find((note) => note.textContent?.startsWith(prefix!)) ?? null;
const press = async (name: string, scope: HTMLElement = document.body) => {
  await act(async () => { fireEvent.click(within(scope).getByRole("button", { name })); });
};
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
  status = { enabled: false, recoveryCodesLeft: 0, requiredBy: new Date(Date.now() + 5 * DAY).toISOString() };
  vi.stubGlobal("fetch", vi.fn((url: string, init: RequestInit = {}) => {
    const key = `${init.method ?? "GET"} ${url.replace(/^\/api\/v1/, "")}`;
    requests.push(key);
    return Promise.resolve(answer(key));
  }));
});

describe("the two-factor deadline banner", () => {
  it("shows the deadline in the browser's time zone while two-factor is off", async () => {
    render();
    await waitFor(() => expect(banner()).toHaveTextContent(t("deadlineBanner", { deadline: inZone(status.requiredBy as string) })));
    expect(inZone(status.requiredBy as string)).not.toBe(
      new Intl.DateTimeFormat("en", { dateStyle: "medium", timeStyle: "short", timeZone: "UTC" }).format(new Date(status.requiredBy as string)));
  });

  it("shows the deadline in the user's language", async () => {
    await act(() => i18n.changeLanguage("es"));
    try {
      render();
      await waitFor(() => expect(screen.getByRole("note")).toHaveTextContent(inZone(status.requiredBy as string, "es")));
    } finally {
      await act(() => i18n.changeLanguage("en"));
    }
  });

  it("is not shown without requiredBy, so not to a Manager or while the requirement is off", async () => {
    delete status.requiredBy;
    render();
    await waitFor(() => expect(requests).toContain("GET /auth/two-factor"));
    await act(() => new Promise((resolve) => setTimeout(resolve, 20)));
    expect(banner()).not.toBeInTheDocument();
  });

  it("is not shown once two-factor is on", async () => {
    status = { ...status, enabled: true, recoveryCodesLeft: 10 };
    render();
    await waitFor(() => expect(requests).toContain("GET /auth/two-factor"));
    await act(() => new Promise((resolve) => setTimeout(resolve, 20)));
    expect(banner()).not.toBeInTheDocument();
  });

  // #1274 decision 18: routing reads requiredBy, never the role or the
  // two_factor claim.
  it("follows the status route whatever the role and the two_factor claim say", async () => {
    render({ sub: "u1", role: "Manager", two_factor: "true" });
    await waitFor(() => expect(banner()).toHaveTextContent(inZone(status.requiredBy as string)));
  });

  it("sends the user to the setup screen once the deadline has passed", async () => {
    status.requiredBy = new Date(Date.now() - DAY).toISOString();
    render();
    expect(await screen.findByRole("heading", { name: t("enrolHeading") })).toBeInTheDocument();
    expect(screen.queryByText("protected home")).not.toBeInTheDocument();
  });

  it("opens the setup on the Account screen, and Back then Forward does not open it again", async () => {
    render();
    await press(i18n.t("common:cancel"), await openSetUp());
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());

    await press("back");
    await screen.findByText("protected home");
    await press("forward");
    await screen.findByRole("heading", { name: t("twoFactorHeading") });
    await act(() => new Promise((resolve) => setTimeout(resolve, 20)));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  // A reload keeps the history entry's state but starts the module afresh.
  it("does not open the setup for a request it did not make", async () => {
    render();
    await press("stale setup");
    await screen.findByRole("heading", { name: t("twoFactorHeading") });
    await act(() => new Promise((resolve) => setTimeout(resolve, 20)));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("goes once the user turns two-factor on through Account in the same session", async () => {
    render();
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
