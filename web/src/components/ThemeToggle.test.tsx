import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { ThemeToggle } from "./ThemeToggle";
import i18n from "../i18n";

beforeEach(() => document.documentElement.removeAttribute("data-theme"));
afterEach(() => document.documentElement.removeAttribute("data-theme"));

describe("ThemeToggle", () => {
  it("flips the root data-theme and its own label on click", () => {
    render(<ThemeToggle />);
    // jsdom has no matchMedia → initial theme is light, so it offers "night"
    fireEvent.click(screen.getByRole("button", { name: "Switch to night mode" }));
    expect(document.documentElement.dataset.theme).toBe("dark");
    expect(screen.getByText("Light")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Switch to light mode" }));
    expect(document.documentElement.dataset.theme).toBe("light");
    expect(screen.getByText("Night")).toBeInTheDocument();
  });

  it("can render icon-only (no text label) while keeping an accessible name", () => {
    render(<ThemeToggle showLabel={false} />);
    const btn = screen.getByRole("button", { name: "Switch to night mode" });
    expect(btn).toBeInTheDocument();
    expect(screen.queryByText("Night")).not.toBeInTheDocument();
  });
});

// ---------------------------------------------------------------------------
// i18n wiring (#182, Task 9, batch B1)
// ---------------------------------------------------------------------------

// `themeToggle` is English-only (not in TRANSLATED_NAMESPACES — see
// translations-status.ts), so under ANY UI language the rendered text falls
// back to this exact English string, same as a still-hardcoded literal would
// render. Asserting that text — even under a non-English locale — would prove
// nothing (CONTRIBUTING-i18n.md's fallback trap). Swap the catalog value at
// runtime instead, the same i18n.addResource technique the other Task 8/9
// wiring tests use, so the marker only renders if ThemeToggle actually reads
// the catalog.
describe("ThemeToggle i18n wiring (#182, Task 9)", () => {
  function withOverride(key: string, value: string, run: () => void) {
    const original = i18n.getResource("en", "themeToggle", key) as string;
    i18n.addResource("en", "themeToggle", key, value);
    try {
      run();
    } finally {
      i18n.addResource("en", "themeToggle", key, original);
    }
  }

  it("reads the switch-to-night aria-label from the catalog, not a hardcoded literal", () => {
    withOverride("switchToNightMode", "NIGHT-ARIA-MARKER", () => {
      render(<ThemeToggle />);
      // jsdom has no matchMedia → initial theme is light, so it offers "night"
      expect(screen.getByRole("button", { name: "NIGHT-ARIA-MARKER" })).toBeInTheDocument();
    });
  });

  it("reads the switch-to-light aria-label from the catalog after toggling", () => {
    withOverride("switchToLightMode", "LIGHT-ARIA-MARKER", () => {
      render(<ThemeToggle />);
      fireEvent.click(screen.getByRole("button", { name: "Switch to night mode" }));
      expect(screen.getByRole("button", { name: "LIGHT-ARIA-MARKER" })).toBeInTheDocument();
    });
  });

  it("reads the night/light label text from the catalog", () => {
    withOverride("night", "NIGHT-LABEL-MARKER", () => {
      render(<ThemeToggle />);
      expect(screen.getByText("NIGHT-LABEL-MARKER")).toBeInTheDocument();

      withOverride("light", "LIGHT-LABEL-MARKER", () => {
        fireEvent.click(screen.getByRole("button", { name: "Switch to night mode" }));
        expect(screen.getByText("LIGHT-LABEL-MARKER")).toBeInTheDocument();
      });
    });
  });
});
