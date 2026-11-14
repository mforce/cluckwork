import { describe, expect, it } from "vitest";
import { screen } from "@testing-library/react";
import { AppLayout } from "./AppLayout";
import { stubMatchMedia } from "../test/matchMedia";
import { renderWithProviders } from "../test/renderWithProviders";

// The phone/desktop switch rides the seam in lib/breakpoints.ts. jsdom 31
// evaluates CSS media queries against a frozen 1024x768 viewport, so the
// `{ xs, md }` switches can no longer be exercised by viewport — tests pick
// an answer through matchMedia instead. Forced phone shows the tab bar and
// drops the sidebar landmark, forced desktop the reverse, and with no
// matchMedia at all (jsdom 30) both stay rendered, as every pre-31 host saw.

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

  it("no matchMedia keeps both navs rendered, the pre-31 contract", () => {
    renderShell();
    expect(screen.queryByRole("complementary")).not.toBeNull();
    expect(screen.queryByRole("navigation", { name: "Sections" })).not.toBeNull();
  });
});
