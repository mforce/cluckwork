import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, within, act } from "@testing-library/react";
import { HelpPage } from "./HelpPage";
import i18n from "../i18n";

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

  it("keeps the contents rail and the sections in step, in document order", () => {
    // The rail is hand-maintained beside the sections it points at ("must
    // mirror the <h3 id=...> sections below, in document order"). A section
    // added without its entry is invisible to anyone navigating by contents,
    // and an entry without its section is a dead link — neither shows up in a
    // test that only asserts the sections it happens to name.
    const { container } = render(<HelpPage />);
    const toc = screen.getByRole("navigation", { name: "Help contents" });

    const linked = within(toc).getAllByRole("link")
      .map((a) => a.getAttribute("href")?.slice(1));
    const sections = Array.from(container.querySelectorAll("h3[id]")).map((h) => h.id);

    expect(sections.length).toBeGreaterThan(0);
    expect(linked).toEqual(sections);
  });

  it("documents farm settings, the currency lock and the logo (#123)", () => {
    render(<HelpPage />);
    expect(screen.getByRole("heading", { name: "Farm settings (admin)", level: 3 })).toBeInTheDocument();
    expect(screen.getByRole("rowheader", { name: "Farm settings" })).toBeInTheDocument();
    expect(screen.getByRole("rowheader", { name: "Currency lock" })).toBeInTheDocument();
    expect(screen.getByRole("rowheader", { name: "Farm logo" })).toBeInTheDocument();
  });

  it("documents the busy-save indicator (#236) via the catalog key, not a drifting literal", () => {
    // The line reads common:workingHint so it can never drift from the
    // BusyButton announcement it explains — swap the catalog value and the
    // page must follow (a hardcoded copy of the sentence would not).
    const original = i18n.getResource("en", "common", "workingHint") as string;
    i18n.addResource("en", "common", "workingHint", "WORKING-HINT-MARKER");
    try {
      render(<HelpPage />);
      expect(screen.getByText("WORKING-HINT-MARKER")).toBeInTheDocument();
    } finally {
      i18n.addResource("en", "common", "workingHint", original);
    }
    // And the real copy renders by default.
    render(<HelpPage />);
    expect(screen.getByText(/A spinning button means the save is still working/)).toBeInTheDocument();
  });

  it("explains the per-account sign-in lock as temporary, without a non-existent admin reset", () => {
    render(<HelpPage />);
    const signIn = screen.getByRole("heading", { name: "Signing in", level: 3 });
    // the lock is described as temporary (wait it out) — the app has no admin
    // password-reset/unlock action, and a reset wouldn't clear the lock anyway.
    expect(screen.getByText(/too many wrong passwords for/i)).toBeInTheDocument();
    expect(screen.getByText(/wait up to about 15 minutes/i)).toBeInTheDocument();
    expect(screen.queryByText(/administrator to set a new password/i)).not.toBeInTheDocument();
    // #145 — the session-persistence + post-update re-login note.
    expect(screen.getByText(/kept in your browser securely/i)).toBeInTheDocument();
    // #169 — the session survives the app being open in several tabs at once.
    expect(screen.getByText(/several tabs/i)).toBeInTheDocument();
    expect(signIn).toBeInTheDocument();
  });

  it("scroll-spies the contents rail — the section in view is marked current", () => {
    render(<HelpPage />);
    const toc = screen.getByRole("navigation", { name: "Help contents" });

    // first item is current by default (Getting around leads the guide now)
    expect(within(toc).getByRole("link", { name: "Getting around" })).toHaveAttribute("aria-current", "location");

    // 'Flocks & birds' scrolls into view → it becomes current, the previous clears
    act(() => ioCallback?.([{ isIntersecting: true, target: { id: "flocks" }, boundingClientRect: { top: 12 } }]));
    const flocks = within(toc).getByRole("link", { name: "Flocks & birds" });
    expect(flocks).toHaveClass("active");
    expect(flocks).toHaveAttribute("aria-current", "location");
    expect(within(toc).getByRole("link", { name: "Getting around" })).not.toHaveAttribute("aria-current");
  });
});
