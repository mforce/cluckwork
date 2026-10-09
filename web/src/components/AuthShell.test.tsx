import { describe, it, expect, afterEach, vi } from "vitest";
import { render } from "@testing-library/react";
import { AuthShell } from "./AuthShell";
import { readThemeTokens } from "../theme/farmTokens";
import { themeColorFrom } from "../theme/metaThemeColor";
import { stubMatchMedia } from "../test/matchMedia";

afterEach(() => vi.unstubAllGlobals());

// #976 round 2 — AuthShell fills the whole viewport in --surface-2/--canvas
// with no sidebar, wrong at desktop width if the meta colour still assumed
// the sidebar-and-shell layout. Which token wins is pinned in
// metaThemeColor.test.ts against the real stylesheet; this proves AuthShell
// itself resyncs on both mount and unmount.
describe("AuthShell (#976 round 2)", () => {
  it("carries the marker syncThemeColorMeta looks for while it is on screen", () => {
    const { container, unmount } = render(
      <AuthShell footerNote="note">content</AuthShell>,
    );
    expect(container.querySelector('[data-meta-surface="auth"]')).not.toBeNull();
    unmount();
    expect(document.querySelector('[data-meta-surface="auth"]')).toBeNull();
  });

  it("resyncs to the auth colour on mount even at desktop layout, then back on unmount", () => {
    stubMatchMedia(true); // desktop — would pick --lavender if AuthShell's marker were ignored
    const meta = document.createElement("meta");
    meta.name = "theme-color";
    document.head.appendChild(meta);
    try {
      const tokens = readThemeTokens();
      const { unmount } = render(<AuthShell footerNote="note">content</AuthShell>);
      expect(meta.content).toBe(themeColorFrom(tokens, true, true));

      unmount();
      expect(meta.content).toBe(themeColorFrom(tokens, true, false));
    } finally {
      meta.remove();
    }
  });

  // #798 (login B) — the next step replaces the tagline in the brand panel and
  // repeats above the form, where a phone shows it.
  it("puts the next step in the brand panel and above the form", () => {
    const { getAllByText, queryByText } = render(
      <AuthShell footerNote="note" nextStep={<span>Next: approve Claude Desktop</span>}>content</AuthShell>);

    expect(getAllByText("Next: approve Claude Desktop")).toHaveLength(2);
    expect(queryByText("Daily entry · Stock · Sales")).not.toBeInTheDocument();
  });
});
