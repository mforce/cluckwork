import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { screen, fireEvent, within } from "@testing-library/react";
import { renderWithProviders } from "../test/renderWithProviders";
import { AppLayout } from "./AppLayout";

// The theme toggle writes data-theme on the document root — reset it between
// tests so one case's choice can't bleed into the next.
beforeEach(() => document.documentElement.removeAttribute("data-theme"));
afterEach(() => document.documentElement.removeAttribute("data-theme"));

// Both navs render at once (CSS, not JS, hides one), so a bare getByRole("link")
// finds a name in the sidebar AND the tab bar. Scope to the landmark under test.
const sidebar = () => within(screen.getByRole("navigation", { name: "Primary" }));
const tabbar = () => within(screen.getByRole("navigation", { name: "Sections" }));

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
