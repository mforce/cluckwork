import { describe, expect, it } from "vitest";
import { screen } from "@testing-library/react";
import { AppLayout } from "./AppLayout";
import { stubMatchMedia } from "../test/matchMedia";
import { responsiveStyle } from "../test/renderedStyle";
import { renderWithProviders } from "../test/renderWithProviders";

// The phone/desktop switch rides the seam in lib/breakpoints.ts. jsdom 31
// evaluates CSS media queries against a frozen 1024x768 viewport, so the
// `{ xs, md }` switches can no longer be exercised by viewport — tests pick
// an answer through matchMedia instead. Forced phone shows the tab bar and
// drops the sidebar landmark, forced desktop the reverse. With no matchMedia
// the seam has no answer and leaves the switch to the CSS media query, which
// jsdom resolves at its 1024px viewport: the desktop layout.

function renderShell() {
  renderWithProviders(<AppLayout />, { route: "/", token: { sub: "u1", role: "Admin" } });
}

describe("the phone mode seam (lib/breakpoints.ts)", () => {
  it("phone shows the tab bar and drops the sidebar landmark", () => {
    stubMatchMedia(false);
    renderShell();
    expect(screen.queryByRole("navigation", { name: "Sections" })).not.toBeNull();
    expect(screen.queryByRole("complementary")).toBeNull();
  });

  it("desktop shows the sidebar landmark and drops the tab bar", () => {
    stubMatchMedia(true);
    renderShell();
    expect(screen.queryByRole("complementary")).not.toBeNull();
    expect(screen.queryByRole("navigation", { name: "Sections" })).toBeNull();
  });

  it("no matchMedia leaves the switch to the media query, desktop at jsdom's 1024px", () => {
    renderShell();
    expect(screen.queryByRole("complementary")).not.toBeNull();
    expect(screen.queryByRole("navigation", { name: "Sections" })).toBeNull();
    // A determined answer sets a literal display and emits no media rule.
    const tabbar = document.querySelector('nav[aria-label="Sections"]')!;
    expect(responsiveStyle(tabbar, "(min-width:0px)", "display")).toBe("block");
    expect(responsiveStyle(tabbar, "(min-width:900px)", "display")).toBe("none");
  });
});
