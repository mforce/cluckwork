import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, within, act } from "@testing-library/react";
import { HelpPage } from "./HelpPage";

// Minimal IntersectionObserver stub (jsdom has none): capture the callback so a
// test can simulate a section scrolling into view.
type IOEntry = { isIntersecting: boolean; target: { id: string }; boundingClientRect: { top: number } };
let ioCallback: ((entries: IOEntry[]) => void) | null = null;

beforeEach(() => {
  ioCallback = null;
  class MockIO {
    constructor(cb: (entries: IOEntry[]) => void) { ioCallback = cb; }
    observe() {}
    unobserve() {}
    disconnect() {}
  }
  vi.stubGlobal("IntersectionObserver", MockIO);
});
afterEach(() => vi.unstubAllGlobals());

describe("HelpPage", () => {
  it("renders the guide with a contents rail linking to its sections", () => {
    render(<HelpPage />);
    expect(screen.getByRole("heading", { name: "Help", level: 2 })).toBeInTheDocument();

    const toc = screen.getByRole("navigation", { name: "Help contents" });
    expect(within(toc).getByRole("link", { name: "The daily loop" })).toHaveAttribute("href", "#daily-loop");

    expect(screen.getByRole("heading", { name: "The daily loop", level: 3 })).toBeInTheDocument();
    expect(screen.getByRole("rowheader", { name: "FIFO" })).toBeInTheDocument();
  });

  it("scroll-spies the contents rail — the section in view is marked current", () => {
    render(<HelpPage />);
    const toc = screen.getByRole("navigation", { name: "Help contents" });

    // first item is current by default
    expect(within(toc).getByRole("link", { name: "The daily loop" })).toHaveAttribute("aria-current", "location");

    // 'Flocks & birds' scrolls into view → it becomes current, the previous clears
    act(() => ioCallback?.([{ isIntersecting: true, target: { id: "flocks" }, boundingClientRect: { top: 12 } }]));
    const flocks = within(toc).getByRole("link", { name: "Flocks & birds" });
    expect(flocks).toHaveClass("active");
    expect(flocks).toHaveAttribute("aria-current", "location");
    expect(within(toc).getByRole("link", { name: "The daily loop" })).not.toHaveAttribute("aria-current");
  });
});
