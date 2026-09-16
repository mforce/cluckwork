import { describe, it, expect, vi } from "vitest";
import { act, render, screen, fireEvent, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import i18n from "../i18n";
import { navGroups, tabEntries } from "../routes/nav";
import { stubMatchMedia } from "../test/matchMedia";
import { BottomNav } from "./BottomNav";

// BottomNav is rendered from the SAME nav model AppLayout uses (nav.tsx), so
// build real groups/tabs rather than hand-rolled fixtures — that keeps this
// suite honest about what a real role actually sees (#148, #182 Task 7).
function renderBottomNav(route = "/", onLogout = vi.fn()) {
  const groups = navGroups("Admin", true);
  const tabs = tabEntries(groups);
  render(
    <MemoryRouter initialEntries={[route]}>
      <BottomNav groups={groups} tabs={tabs} onLogout={onLogout} />
    </MemoryRouter>,
  );
  return { groups, tabs, onLogout };
}

// Both the tab bar and (once opened) the More sheet render a <nav> — scope to
// the landmark under test rather than a bare role query, same reasoning as
// AppLayout.test.tsx.
const tabbar = () => within(screen.getByRole("navigation", { name: "Sections" }));

describe("BottomNav", () => {
  it("renders the four highest-priority destinations an admin reaches, plus More", () => {
    renderBottomNav();
    const tabs = tabbar().getAllByRole("link").map((a) => a.textContent);
    expect(tabs).toEqual(["Daily entry", "Stock", "Sales", "History"]);
    expect(tabbar().getByRole("button", { name: "More" })).toBeInTheDocument();
  });

  it("opens the More sheet with the full grouped nav and a way to sign out", () => {
    renderBottomNav();
    expect(screen.queryByRole("dialog")).toBeNull();

    fireEvent.click(tabbar().getByRole("button", { name: "More" }));

    const sheet = within(screen.getByRole("dialog"));
    expect(sheet.getByRole("heading", { name: "Menu" })).toBeInTheDocument();
    // The complete map, including admin-only Setup destinations that are
    // never tabs.
    expect(sheet.getByRole("link", { name: "Grades" })).toBeInTheDocument();
    expect(sheet.getByRole("link", { name: "Export" })).toBeInTheDocument();
    expect(sheet.getByRole("button", { name: "Sign out" })).toBeInTheDocument();
    expect(
      within(screen.getByRole("dialog")).getByRole("navigation", { name: "All sections" }),
    ).toBeInTheDocument();
  });

  it("closes the More sheet when a destination link is chosen", () => {
    renderBottomNav();
    fireEvent.click(tabbar().getByRole("button", { name: "More" }));

    fireEvent.click(within(screen.getByRole("dialog")).getByRole("link", { name: "Grades" }));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("calls onLogout and closes the sheet when Sign out is chosen", () => {
    const { onLogout } = renderBottomNav();
    fireEvent.click(tabbar().getByRole("button", { name: "More" }));

    fireEvent.click(within(screen.getByRole("dialog")).getByRole("button", { name: "Sign out" }));
    expect(onLogout).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("marks More as current when the screen is not one of the tabs", () => {
    // /grades is an admin overflow route — no tab points at it, so the bar
    // would otherwise show nothing active there.
    renderBottomNav("/grades");
    const more = tabbar().getByRole("button", { name: "More" });
    expect(more).toHaveAttribute("aria-current", "page");
    expect(more).toHaveClass("active");
  });

  it("does not mark More current when a tab owns the screen", () => {
    renderBottomNav("/stock");
    expect(tabbar().getByRole("button", { name: "More" })).not.toHaveAttribute("aria-current");
  });

  // #883 round 2, finding 2: the resize listener used to read 901px against
  // the 900px `md` breakpoint the sidebar/tab-bar switch itself uses — at
  // exactly 900px the CSS already hides the tab bar under the now-visible
  // sidebar while the listener's own ">900" check stayed false, leaving the
  // sheet open with its trigger hidden underneath. Both now read MD_UP_QUERY.
  it("closes an open More sheet when the width crosses exactly the md breakpoint (900px)", () => {
    const media = stubMatchMedia(false); // starts below md
    renderBottomNav();
    // Pins the actual boundary asked for — a fixed `matches` stub alone
    // cannot tell a 901px listener from a 900px one, since either gets the
    // same answer here; only the query string itself proves which was used.
    expect(media.matchMedia).toHaveBeenCalledWith("(min-width: 900px)");
    fireEvent.click(tabbar().getByRole("button", { name: "More" }));
    expect(screen.getByRole("dialog")).toBeInTheDocument();

    act(() => media.triggerChange(true)); // crosses to >= 900px
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("leaves an open More sheet open on a resize that stays below the md breakpoint", () => {
    const media = stubMatchMedia(false);
    renderBottomNav();
    fireEvent.click(tabbar().getByRole("button", { name: "More" }));

    act(() => media.triggerChange(false)); // still < 900px
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });

  // #883 round 2, finding 3: the tab bar used to take the WHOLE --tabbar-h
  // value (3.6rem content + the safe-area inset) as its own height, centring
  // the actions inside the inset and sitting the labels under a notched
  // phone's home indicator. Content now gets exactly 3.6rem; the inset is its
  // own bottom padding underneath.
  it("gives the bar a 3.6rem content height with the safe-area inset as its own bottom padding", () => {
    renderBottomNav();
    const bar = document.querySelector(".MuiBottomNavigation-root") as HTMLElement;
    expect(bar).toBeTruthy();
    // jsdom resolves a simple rem declaration against its default 16px root
    // font size rather than echoing the unit back.
    expect(getComputedStyle(bar).height).toBe("57.6px"); // 3.6rem
    // jsdom's CSS parser does not understand `env(...)` (it silently drops the
    // declaration from computed style, reading back "0"), so the padding half
    // of the split is read from the real emotion stylesheet instead — the
    // same workaround FarmThemeProvider.render.test.tsx uses for a value
    // jsdom's limited CSSOM cannot resolve.
    const emotionClass = Array.from(bar.classList).find((c) => c.startsWith("css-"));
    expect(emotionClass).toBeTruthy();
    const css = Array.from(document.querySelectorAll("style[data-emotion]"))
      .map((tag) => tag.textContent ?? "").join("\n");
    const rule = css.split("}").find((block) => block.includes(`.${emotionClass}`));
    expect(rule).toContain("padding-bottom:env(safe-area-inset-bottom)");
  });

  describe("i18n wiring (#182, Task 7)", () => {
    // `nav` is English-only (see translations-status.ts), so the English text
    // renders identically whether it comes from the catalog or a leftover
    // hardcoded literal — asserting English proves nothing. Each test below
    // swaps ONE catalog value at runtime (i18n.addResource, the same
    // mechanism i18n.test.ts uses for its fallback test) and asserts the
    // swapped MARKER renders, which only happens if the component reads the
    // catalog at render time.
    function withNavOverride(key: string, value: string, run: () => void) {
      const original = i18n.getResource("en", "nav", key) as string;
      i18n.addResource("en", "nav", key, value);
      try {
        run();
      } finally {
        i18n.addResource("en", "nav", key, original);
      }
    }

    it("reads a tab label from the catalog, not a hardcoded literal", () => {
      withNavOverride("stock", "NAV-STOCK-MARKER", () => {
        renderBottomNav();
        expect(tabbar().getByRole("link", { name: "NAV-STOCK-MARKER" })).toBeInTheDocument();
        expect(tabbar().queryByRole("link", { name: "Stock" })).not.toBeInTheDocument();
      });
    });

    it("reads the tab bar's landmark aria-label from the catalog", () => {
      withNavOverride("tabBarAriaLabel", "NAV-SECTIONS-MARKER", () => {
        renderBottomNav();
        expect(screen.getByRole("navigation", { name: "NAV-SECTIONS-MARKER" })).toBeInTheDocument();
      });
    });

    it("reads the More button, the sheet's title, and its landmark from the catalog", () => {
      const overrides: [string, string][] = [
        ["moreButton", "NAV-MORE-MARKER"],
        ["menuTitle", "NAV-MENU-MARKER"],
        ["allSectionsAriaLabel", "NAV-ALLSECTIONS-MARKER"],
      ];
      const originals = overrides.map(([key]) => i18n.getResource("en", "nav", key) as string);
      for (const [key, value] of overrides) i18n.addResource("en", "nav", key, value);
      try {
        renderBottomNav();
        fireEvent.click(tabbar().getByRole("button", { name: "NAV-MORE-MARKER" }));
        const sheet = within(screen.getByRole("dialog"));
        expect(sheet.getByRole("heading", { name: "NAV-MENU-MARKER" })).toBeInTheDocument();
        expect(
          within(screen.getByRole("dialog")).getByRole("navigation", { name: "NAV-ALLSECTIONS-MARKER" }),
        ).toBeInTheDocument();
      } finally {
        overrides.forEach(([key], i) => i18n.addResource("en", "nav", key, originals[i]));
      }
    });

    it("reads a group heading (More sheet) and Sign out from the catalog", () => {
      withNavOverride("groupSetup", "NAV-GROUPSETUP-MARKER", () => {
        withNavOverride("signOut", "NAV-SIGNOUT-MARKER", () => {
          renderBottomNav();
          fireEvent.click(tabbar().getByRole("button", { name: "More" }));
          const sheet = within(screen.getByRole("dialog"));
          expect(sheet.getByText("NAV-GROUPSETUP-MARKER")).toBeInTheDocument();
          expect(sheet.getByRole("button", { name: "NAV-SIGNOUT-MARKER" })).toBeInTheDocument();
        });
      });
    });
  });
});
