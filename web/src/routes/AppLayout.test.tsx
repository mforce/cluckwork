import { lazy, type ReactElement } from "react";
import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { act, render, screen, fireEvent, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router";
import { renderWithProviders } from "../test/renderWithProviders";
import { AuthProvider } from "../auth/AuthContext";
import { FarmContext } from "../farm/FarmContext";
import { account, farmState } from "../test/fixtures";
import i18n from "../i18n";
import { AppLayout } from "./AppLayout";

// The theme toggle writes data-theme on the document root — reset it between
// tests so one case's choice can't bleed into the next.
beforeEach(() => document.documentElement.removeAttribute("data-theme"));
afterEach(() => document.documentElement.removeAttribute("data-theme"));

// Both navs render at once (CSS, not JS, hides one), so a bare getByRole("link")
// finds a name in the sidebar AND the tab bar. Scope to the landmark under test.
const sidebar = () => within(screen.getByRole("navigation", { name: "Primary" }));
const tabbar = () => within(screen.getByRole("navigation", { name: "Sections" }));

describe("AppLayout lazy route containment (#595)", () => {
  it("shows catalog loading status only in the content pane while shell navigation remains", async () => {
    let resolveScreen!: (value: { default: () => ReactElement }) => void;
    const DeferredScreen = lazy(() => new Promise<{ default: () => ReactElement }>((resolve) => {
      resolveScreen = resolve;
    }));
    const original = i18n.getResource("en", "common", "loading") as string;
    i18n.addResource("en", "common", "loading", "ROUTE-LOADING-MARKER");

    try {
      renderWithProviders(
        <Routes>
          <Route element={<AppLayout />}>
            <Route index element={<p>Resolved dashboard</p>} />
            <Route path="stock" element={<DeferredScreen />} />
          </Route>
        </Routes>,
        { token: { sub: "u1", role: "Admin" } },
      );

      expect(screen.getByText("Resolved dashboard")).toBeInTheDocument();
      fireEvent.click(sidebar().getByRole("link", { name: "Stock" }));

      expect(await screen.findByRole("status")).toHaveTextContent("ROUTE-LOADING-MARKER");
      expect(screen.queryByText("Resolved dashboard")).not.toBeInTheDocument();
      expect(sidebar().getByRole("link", { name: "Dashboard" })).toBeInTheDocument();

      await act(async () => {
        resolveScreen({ default: () => <p>Deferred stock</p> });
      });
      expect(await screen.findByText("Deferred stock")).toBeInTheDocument();
      expect(screen.queryByRole("status")).not.toBeInTheDocument();
    } finally {
      i18n.addResource("en", "common", "loading", original);
    }
  });

  it("contains a rejected route module in the screen boundary while the shell survives", async () => {
    const RejectedScreen = lazy(() => Promise.reject(new Error("ROUTE-CHUNK-REJECTION")));
    const consoleError = vi.spyOn(console, "error").mockImplementation(() => undefined);

    try {
      renderWithProviders(
        <Routes>
          <Route element={<AppLayout />}>
            <Route index element={<p>Resolved dashboard</p>} />
            <Route path="stock" element={<RejectedScreen />} />
          </Route>
        </Routes>,
        { token: { sub: "u1", role: "Admin" } },
      );

      fireEvent.click(sidebar().getByRole("link", { name: "Stock" }));

      const fallback = await screen.findByRole("alert");
      expect(fallback).toHaveTextContent("Something went wrong");
      expect(fallback).toHaveTextContent("ROUTE-CHUNK-REJECTION");
      expect(sidebar().getByRole("link", { name: "Dashboard" })).toBeInTheDocument();
    } finally {
      consoleError.mockRestore();
    }
  });
});

describe("AppLayout sidebar", () => {
  it("groups the nav and gates links by role — an admin sees Setup destinations", () => {
    renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" } });
    expect(sidebar().getByRole("link", { name: "Dashboard" })).toBeInTheDocument();
    expect(sidebar().getByRole("link", { name: "Users" })).toBeInTheDocument();
    expect(sidebar().getByRole("link", { name: "Daily entry" })).toBeInTheDocument();
  });

  it("hides production + admin destinations from a ReadOnly role", () => {
    renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "ReadOnly" } });
    expect(sidebar().getByRole("link", { name: "Stock" })).toBeInTheDocument();
    // Gated links are absent from BOTH navs, not just the sidebar.
    expect(screen.queryByRole("link", { name: "Daily entry" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Users" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Sales" })).not.toBeInTheDocument();
  });

  it("brands the shell with the app's own name when no farm has loaded (#123)", () => {
    // No FarmProvider here — the default context, which is also what the real
    // shell shows while /account is in flight and after one that failed.
    renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" } });
    expect(screen.getByText("Cluckwork")).toBeInTheDocument();
    expect(screen.queryByRole("presentation")).not.toBeInTheDocument();
  });

  it("brands the shell with the farm's own name once it is known", () => {
    renderWithProviders(<AppLayout />, {
      token: { sub: "u1", role: "Admin" },
      farm: account({ name: "Hen House" }),
    });
    expect(screen.getByText("Hen House")).toBeInTheDocument();
    expect(screen.queryByText("Cluckwork")).not.toBeInTheDocument();
  });

  it("says so — and offers the read again — when the farm could not be loaded", async () => {
    // Silence here is the failure: without a banner the pickers follow the
    // DEVICE's day and the screen looks perfectly healthy (codex review of
    // #123).
    let retried = 0;
    render(
      <MemoryRouter>
        <AuthProvider>
          <FarmContext.Provider value={farmState({
            farm: null, loadFailed: true, refresh: async () => { retried += 1; return true; },
          })}>
            <AppLayout />
          </FarmContext.Provider>
        </AuthProvider>
      </MemoryRouter>);

    const banner = screen.getByRole("alert");
    expect(banner).toHaveTextContent(/dates follow this device rather than the farm/);
    fireEvent.click(within(banner).getByRole("button", { name: "Try again" }));
    expect(retried).toBe(1);
  });

  it("says the farm may be out of date when a LATER read failed", async () => {
    // The interesting half: a farm is still held (the provider keeps the last
    // good one on purpose), so a banner keyed on `farm === null` stays silent
    // while the timezone on screen is the one a save was meant to replace
    // (round 2: codex + pi).
    render(
      <MemoryRouter>
        <AuthProvider>
          <FarmContext.Provider value={farmState({
            farm: account({ name: "Hen House" }), loadFailed: true,
          })}>
            <AppLayout />
          </FarmContext.Provider>
        </AuthProvider>
      </MemoryRouter>);

    expect(screen.getByRole("alert")).toHaveTextContent(/may be out of date/);
    // ...and not the never-loaded wording, which would be wrong here.
    expect(screen.queryByText(/dates follow this device/)).not.toBeInTheDocument();
  });

  it("says nothing about the farm while one is loaded", () => {
    renderWithProviders(<AppLayout />, {
      token: { sub: "u1", role: "Admin" },
      farm: account({ name: "Hen House" }),
    });
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("shows no version line when VITE_APP_VERSION is unset (#458 — dev/test builds)", () => {
    // import.meta.env.VITE_APP_VERSION is read once at module scope
    // (AppLayout.tsx), matching errorReport.ts's own "absent in dev builds"
    // contract — this test environment never sets it, so this proves the
    // component renders nothing rather than a literal "vundefined".
    renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" } });
    // Matches either a real version ("v0.0.2") or i18next's literal rendering
    // of a missing interpolation (a bare "v", the {{version}} slot rendering
    // empty) — both must be absent. Measured directly: i18next does not
    // render "vundefined" for a missing var, it renders an empty
    // interpolation, so a regex assuming the JS-string-concat shape would
    // have silently passed against the exact bug this guards.
    expect(screen.queryByText(/^v(\d.*)?$/)).not.toBeInTheDocument();
  });

  it("toggles light ↔ night, flipping the control and the root data-theme", () => {
    renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" } });
    // jsdom has no matchMedia → initial theme resolves to light, so the control
    // offers "night". The sidebar's toggle is the one on screen; the More
    // sheet's copy only exists while the sheet is open.
    fireEvent.click(screen.getByRole("button", { name: "Switch to night mode" }));
    expect(document.documentElement.dataset.theme).toBe("dark");

    fireEvent.click(screen.getByRole("button", { name: "Switch to light mode" }));
    expect(document.documentElement.dataset.theme).toBe("light");
  });
});

describe("AppLayout bottom tabs", () => {
  it("promotes the four most-used destinations a producer can reach, plus More", () => {
    renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" } });
    const tabs = tabbar().getAllByRole("link").map((a) => a.textContent);
    expect(tabs).toEqual(["Daily entry", "Stock", "Sales", "History"]);
    expect(tabbar().getByRole("button", { name: "More" })).toBeInTheDocument();
  });

  it("adapts the tabs to a role that cannot produce or sell", () => {
    renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "ReadOnly" } });
    const tabs = tabbar().getAllByRole("link").map((a) => a.textContent);
    // No Daily entry (can't produce), no Sales (ReadOnly) — the bar backfills
    // with what this role actually reaches, in priority order.
    expect(tabs).toEqual(["Stock", "History", "Dashboard", "Reports"]);
  });

  it("opens the More sheet with the full grouped nav and a way out", () => {
    renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" } });
    expect(screen.queryByRole("dialog")).toBeNull();

    fireEvent.click(tabbar().getByRole("button", { name: "More" }));

    const sheet = within(screen.getByRole("dialog"));
    // The complete map lives here, including the admin-only Setup destinations
    // that are never tabs.
    expect(sheet.getByRole("link", { name: "Grades" })).toBeInTheDocument();
    expect(sheet.getByRole("link", { name: "Export" })).toBeInTheDocument();
    expect(sheet.getByRole("button", { name: "Sign out" })).toBeInTheDocument();
  });

  it("closes the More sheet when a destination is chosen", () => {
    renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" } });
    fireEvent.click(tabbar().getByRole("button", { name: "More" }));

    fireEvent.click(within(screen.getByRole("dialog")).getByRole("link", { name: "Grades" }));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("marks More as current when the screen is not one of the tabs", () => {
    // /grades is an admin overflow route — no tab points at it, so the bar
    // would otherwise show nothing active there.
    renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" }, route: "/grades" });
    const more = tabbar().getByRole("button", { name: "More" });
    expect(more).toHaveAttribute("aria-current", "page");
    expect(more).toHaveClass("active");
  });

  it("does not mark More current when a tab owns the screen", () => {
    renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" }, route: "/stock" });
    expect(tabbar().getByRole("button", { name: "More" })).not.toHaveAttribute("aria-current");
  });
});

describe("AppLayout i18n wiring (#182, Task 7)", () => {
  // `nav` is English-only (not in TRANSLATED_NAMESPACES — see
  // translations-status.ts), so under ANY UI language the rendered text falls
  // back to the exact same English string a still-hardcoded control would
  // also render. Asserting that English text — even under a non-English
  // locale — would prove nothing (CONTRIBUTING-i18n.md's fallback trap). So
  // each test below swaps ONE catalog value at runtime — the same
  // i18n.addResource mechanism i18n.test.ts uses for its own fallback test —
  // and asserts the swapped MARKER renders. That only happens if the render
  // path actually reads the catalog; a hardcoded literal would keep showing
  // the original English no matter what the catalog holds.
  function withNavOverride(key: string, value: string, run: () => void) {
    const original = i18n.getResource("en", "nav", key) as string;
    i18n.addResource("en", "nav", key, value);
    try {
      run();
    } finally {
      i18n.addResource("en", "nav", key, original);
    }
  }

  it("reads a sidebar destination label from the catalog, not a hardcoded literal", () => {
    withNavOverride("dashboard", "NAV-DASHBOARD-MARKER", () => {
      renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" } });
      expect(sidebar().getByRole("link", { name: "NAV-DASHBOARD-MARKER" })).toBeInTheDocument();
      expect(sidebar().queryByRole("link", { name: "Dashboard" })).not.toBeInTheDocument();
    });
  });

  it("reads a section heading from the catalog", () => {
    withNavOverride("groupOverview", "NAV-GROUP-MARKER", () => {
      renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" } });
      expect(sidebar().getByText("NAV-GROUP-MARKER")).toBeInTheDocument();
      expect(sidebar().queryByText("Overview")).not.toBeInTheDocument();
    });
  });

  it("reads the sidebar's landmark aria-label from the catalog", () => {
    withNavOverride("primaryNavAriaLabel", "NAV-PRIMARY-MARKER", () => {
      renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" } });
      expect(screen.getByRole("navigation", { name: "NAV-PRIMARY-MARKER" })).toBeInTheDocument();
    });
  });

  it("reads Sign out from the catalog", () => {
    withNavOverride("signOut", "NAV-SIGNOUT-MARKER", () => {
      renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" } });
      expect(screen.getByRole("button", { name: "NAV-SIGNOUT-MARKER" })).toBeInTheDocument();
    });
  });

  it("reads the skip-to-content link's text from the catalog", () => {
    withNavOverride("skipToContent", "NAV-SKIP-MARKER", () => {
      renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" } });
      expect(screen.getByRole("link", { name: "NAV-SKIP-MARKER" })).toHaveAttribute(
        "href",
        "#main-content",
      );
    });
  });

  it("reads the never-loaded farm banner and its retry button from the catalog", () => {
    withNavOverride("farmLoadFailedNeverLoaded", "NAV-NEVERLOADED-MARKER", () => {
      withNavOverride("tryAgain", "NAV-TRYAGAIN-MARKER", () => {
        render(
          <MemoryRouter>
            <AuthProvider>
              <FarmContext.Provider value={farmState({
                farm: null, loadFailed: true, refresh: async () => true,
              })}>
                <AppLayout />
              </FarmContext.Provider>
            </AuthProvider>
          </MemoryRouter>);
        expect(screen.getByRole("alert")).toHaveTextContent("NAV-NEVERLOADED-MARKER");
        expect(screen.getByRole("button", { name: "NAV-TRYAGAIN-MARKER" })).toBeInTheDocument();
      });
    });
  });

  it("reads the stale-farm banner variant from the catalog", () => {
    withNavOverride("farmLoadFailedStale", "NAV-STALE-MARKER", () => {
      render(
        <MemoryRouter>
          <AuthProvider>
            <FarmContext.Provider value={farmState({
              farm: account({ name: "Hen House" }), loadFailed: true,
            })}>
              <AppLayout />
            </FarmContext.Provider>
          </AuthProvider>
        </MemoryRouter>);
      expect(screen.getByRole("alert")).toHaveTextContent("NAV-STALE-MARKER");
    });
  });

  it("composes document.title from the active nav entry's catalog label and the shared suffix", () => {
    renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" }, route: "/stock" });
    expect(document.title).toBe(`${i18n.t("nav:stock")}${i18n.t("nav:titleSuffix")}`);
  });

  it("reads the per-page title label from the catalog, not a hardcoded literal", () => {
    withNavOverride("stock", "NAV-STOCK-TITLE-MARKER", () => {
      renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" }, route: "/stock" });
      expect(document.title.startsWith("NAV-STOCK-TITLE-MARKER")).toBe(true);
      expect(document.title.startsWith("Stock")).toBe(false);
    });
  });

  it("reads the title suffix from the catalog, not a hardcoded ' — Cluckwork'", () => {
    withNavOverride("titleSuffix", " :: NAV-SUFFIX-MARKER", () => {
      renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" } });
      expect(document.title.endsWith(" :: NAV-SUFFIX-MARKER")).toBe(true);
    });
  });
});
