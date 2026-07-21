import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { screen, fireEvent } from "@testing-library/react";
import { renderWithProviders } from "../test/renderWithProviders";
import { AppLayout } from "./AppLayout";

// The theme toggle writes data-theme on the document root — reset it between
// tests so one case's choice can't bleed into the next.
beforeEach(() => document.documentElement.removeAttribute("data-theme"));
afterEach(() => document.documentElement.removeAttribute("data-theme"));

describe("AppLayout", () => {
  it("groups the nav and gates links by role — an admin sees Setup destinations", () => {
    renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" } });
    expect(screen.getByRole("link", { name: "Dashboard" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Users" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Daily entry" })).toBeInTheDocument();
  });

  it("hides production + admin destinations from a ReadOnly role", () => {
    renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "ReadOnly" } });
    expect(screen.getByRole("link", { name: "Stock" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Daily entry" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Users" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Sales" })).not.toBeInTheDocument();
  });

  it("toggles light ↔ night, flipping the control and the root data-theme", () => {
    renderWithProviders(<AppLayout />, { token: { sub: "u1", role: "Admin" } });
    // jsdom has no matchMedia → initial theme resolves to light, so the control
    // offers "night".
    fireEvent.click(screen.getByRole("button", { name: "Switch to night mode" }));
    expect(document.documentElement.dataset.theme).toBe("dark");

    // control now offers the way back
    fireEvent.click(screen.getByRole("button", { name: "Switch to light mode" }));
    expect(document.documentElement.dataset.theme).toBe("light");
  });
});
