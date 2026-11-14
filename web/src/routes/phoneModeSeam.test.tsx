import { describe, expect, it, afterEach } from "vitest";
import { screen } from "@testing-library/react";
import { AppLayout } from "./AppLayout";
import { setPhoneModeForTests } from "../lib/breakpoints";
import { renderWithProviders } from "../test/renderWithProviders";

// The phone/desktop switch rides the forceable seam in lib/breakpoints.ts.
// jsdom 31 evaluates media queries against a frozen 1024x768 viewport, so the
// `{ xs, md }` switches can no longer be exercised by viewport — tests force
// an answer instead. These cases pin the seam's contract: forced phone shows
// the tab bar and drops the sidebar landmark, forced desktop the reverse, and
// with no answer at all (jsdom 30 and other matchMedia-less hosts) both stay
// rendered, which is what every pre-31 host saw.

afterEach(() => setPhoneModeForTests(null));

function renderShell() {
  renderWithProviders(<AppLayout />, { route: "/", token: { sub: "u1", role: "Admin" } });
}

describe("the forceable phone mode (lib/breakpoints.ts)", () => {
  it("forced phone shows the tab bar and drops the sidebar landmark", () => {
    setPhoneModeForTests(true);
    renderShell();
    expect(screen.queryByRole("navigation", { name: "Sections" })).not.toBeNull();
    expect(screen.queryByRole("complementary")).toBeNull();
  });

  it("forced desktop shows the sidebar landmark and drops the tab bar", () => {
    setPhoneModeForTests(false);
    renderShell();
    expect(screen.queryByRole("complementary")).not.toBeNull();
    expect(screen.queryByRole("navigation", { name: "Sections" })).toBeNull();
  });

  it("no answer keeps both navs rendered, the pre-31 contract", () => {
    renderShell();
    expect(screen.queryByRole("complementary")).not.toBeNull();
    expect(screen.queryByRole("navigation", { name: "Sections" })).not.toBeNull();
  });
});
