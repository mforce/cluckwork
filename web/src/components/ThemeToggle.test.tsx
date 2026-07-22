import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent } from "@testing-library/react";
import { ThemeToggle } from "./ThemeToggle";

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
