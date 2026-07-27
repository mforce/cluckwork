import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { SessionProvider, useMe } from "./SessionContext";
import { AuthProvider } from "../auth/AuthContext";
import { useFarm } from "../farm/useFarm";
import { setStoredToken } from "../test/jwt";
import * as api from "../api/cluckwork";
import { account } from "../test/fixtures";
import i18n from "../i18n";

vi.mock("../api/cluckwork", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../api/cluckwork")>();
  return { ...actual, getMe: vi.fn(), getAccount: vi.fn() };
});
const mockGetMe = vi.mocked(api.getMe);
const mockGetAccount = vi.mocked(api.getAccount);

function Probe() {
  const me = useMe();
  const { farm, loadFailed } = useFarm(); // works: Probe is inside FarmProvider
  return (
    <div>
      <span data-testid="who">{me ? me.email : "no-me"}</span>
      <span data-testid="farm">{farm ? farm.name : loadFailed ? "farm-failed" : "none"}</span>
    </div>
  );
}
const renderShell = () =>
  render(
    <MemoryRouter>
      <AuthProvider>
        <SessionProvider><Probe /></SessionProvider>
      </AuthProvider>
    </MemoryRouter>,
  );

beforeEach(() => {
  vi.clearAllMocks();
  setStoredToken({ sub: "u1", role: "Admin" });
});

describe("SessionProvider", () => {
  it("gates the shell until /me + /account resolve, then reveals it with the user", async () => {
    let resolveMe!: (m: api.Me) => void;
    mockGetMe.mockReturnValue(new Promise((r) => { resolveMe = r; }));
    mockGetAccount.mockResolvedValue(account());

    renderShell();
    // Gated: nothing rendered while the reads are in flight.
    expect(screen.queryByTestId("who")).toBeNull();

    resolveMe({ id: "u1", email: "a@b.co", name: null, role: "Admin", language: null });
    await waitFor(() => expect(screen.getByTestId("who")).toHaveTextContent("a@b.co"));
  });

  it("fetches /me and /account concurrently (both started before either settles)", async () => {
    let meStarted = false, accountStarted = false;
    mockGetMe.mockImplementation(() => { meStarted = true; return new Promise(() => {}); });
    mockGetAccount.mockImplementation(() => { accountStarted = true; return new Promise(() => {}); });
    renderShell();
    await waitFor(() => expect(meStarted && accountStarted).toBe(true));
  });

  it("reveals the shell on a fully failed read (English fallback), never a permanent blank", async () => {
    mockGetMe.mockRejectedValue(new Error("boom"));
    mockGetAccount.mockRejectedValue(new Error("boom"));
    renderShell();
    await waitFor(() => expect(screen.getByTestId("who")).toHaveTextContent("no-me"));
    expect(screen.getByTestId("farm")).toHaveTextContent("farm-failed");
    expect(i18n.language).toBe("en");
  });

  it("preserves a good /account when /me fails (independent settle)", async () => {
    mockGetMe.mockRejectedValue(new Error("boom"));
    mockGetAccount.mockResolvedValue(account({ name: "Sunrise Farm" }));
    renderShell();
    await waitFor(() => expect(screen.getByTestId("farm")).toHaveTextContent("Sunrise Farm"));
    // /me failed → no user, but the farm (timezone/locale source) is NOT lost.
    expect(screen.getByTestId("who")).toHaveTextContent("no-me");
  });

  it("does not switch language or set state when unmounted before the reads settle (cancellation)", async () => {
    let resolveMe!: (m: api.Me) => void;
    mockGetMe.mockReturnValue(new Promise((r) => { resolveMe = r; }));
    mockGetAccount.mockResolvedValue(account());
    const changeSpy = vi.spyOn(i18n, "changeLanguage");
    const { unmount } = renderShell();
    unmount(); // tear down while /me is still in flight (allSettled not resolved yet)
    changeSpy.mockClear(); // ignore anything before unmount
    resolveMe({ id: "u1", email: "a@b.co", name: null, role: "Admin", language: null });
    await Promise.resolve();
    await Promise.resolve();
    // The cancelled guard fires right after allSettled, BEFORE changeLanguage.
    expect(changeSpy).not.toHaveBeenCalled();
    changeSpy.mockRestore();
  });

  it("resolves the user's language before revealing the shell", async () => {
    // en-only today, but prove changeLanguage ran with the resolved value.
    const spy = vi.spyOn(i18n, "changeLanguage");
    mockGetMe.mockResolvedValue({ id: "u1", email: "a@b.co", name: null, role: "Admin", language: "en" });
    mockGetAccount.mockResolvedValue(account({ locale: "en-US" }));
    renderShell();
    await waitFor(() => expect(screen.getByTestId("who")).toHaveTextContent("a@b.co"));
    expect(spy).toHaveBeenCalledWith("en");
  });
});
