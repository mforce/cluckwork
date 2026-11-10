import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { act, fireEvent, screen, waitFor } from "@testing-library/react";
import { Route, Routes } from "react-router";
import { ProtectedRoute } from "./ProtectedRoute";
import { TwoFactorDeadlineBanner } from "./TwoFactorDeadlineBanner";
import { AccountPage } from "./AccountPage";
import { renderWithProviders } from "../test/renderWithProviders";
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

let status: Record<string, unknown>;
let requests: string[];

const tree = () => (
  <Routes>
    <Route element={<ProtectedRoute />}>
      <Route path="/" element={<><TwoFactorDeadlineBanner /><div>protected home</div></>} />
      <Route path="/account" element={<AccountPage />} />
    </Route>
  </Routes>
);
const render = (token: Record<string, unknown> = { sub: "u1", role: "Admin" }) => renderWithProviders(tree(), { route: "/", token });
const banner = () => screen.queryByRole("note");

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
    return Promise.resolve(key === "GET /auth/two-factor" ? json(status) : json([]));
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
      await waitFor(() => expect(banner()).toHaveTextContent(inZone(status.requiredBy as string, "es")));
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
    expect(await screen.findByRole("note")).toHaveTextContent(inZone(status.requiredBy as string));
  });

  it("sends the user to the setup screen once the deadline has passed", async () => {
    status.requiredBy = new Date(Date.now() - DAY).toISOString();
    render();
    expect(await screen.findByRole("heading", { name: t("enrolHeading") })).toBeInTheDocument();
    expect(screen.queryByText("protected home")).not.toBeInTheDocument();
  });

  it("opens the setup on the Account screen", async () => {
    render();
    const setUp = await screen.findByRole("button", { name: t("setUpButton") });
    await act(async () => { fireEvent.click(setUp); });
    expect(await screen.findByRole("dialog", { name: t("setUpTitle") })).toBeInTheDocument();
  });
});
