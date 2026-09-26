// web/src/components/ExpandedLayRate.test.tsx
import { describe, it, expect, vi, beforeEach } from "vitest";
import { useState } from "react";
import { act, fireEvent, screen, within } from "@testing-library/react";
import { renderWithProviders } from "../test/renderWithProviders";
import { daysBefore } from "../lib/dates";
import { DEFAULT_LOCALE, formatDate } from "../lib/format";
import { stubMatchMedia } from "../test/matchMedia";
import { ExpandedLayRate } from "./ExpandedLayRate";
import { anyDialogOpen } from "./Dialog";
import { useMissedAnnouncement } from "./useMissedAnnouncement";
import { DAY_GAP_PX, DAY_SLOT_PX } from "../lib/dayWindow";
import type { DayStripData, DayStripSlot } from "../lib/dashboard";

// 90 recorded days ending on the farm's yesterday, every one complete, so the
// only thing under test is where the WINDOW is.
const DAYS = 90;
const END = "2026-09-22";
const iso = (index: number) => daysBefore(END, DAYS - 1 - index);
const shown = (index: number) => formatDate(iso(index), DEFAULT_LOCALE, null);
const slots: DayStripSlot[] = Array.from({ length: DAYS }, (_, i) => ({
  kind: "recorded", date: iso(i), eggs: 2000 + i, heightPct: 90, weekBreak: i > 0 && i % 7 === 0,
}));
const data: DayStripData = {
  slots, max: 2137, average: 2052.4, averagePct: 96,
  complete: DAYS, partial: 0, unrecorded: 0, scale: "complete",
};

// `Modal` portals the frame to document.body, so nothing here is inside RTL's
// own container.
const frame = () => screen.getByRole("dialog");

const props = (over: Partial<Parameters<typeof ExpandedLayRate>[0]> = {}) => ({
  data, failed: false, label: "Eggs per day", title: "Eggs per day · complete-day scale",
  peak: "Peak 2,137", average: "Complete-day avg 2,052.4",
  legend: { complete: "Complete", partial: "Partial", noEntry: "No entry" },
  tip: (slot: DayStripSlot) => `${slot.date} readout`,
  scopeName: "All flocks", from: slots[0].date, to: slots[DAYS - 1].date,
  latestDay: slots[DAYS - 1].date, range: { kind: "preset", days: 30 } as const,
  onRangeChange: () => {}, onClose: () => {},
  ...over,
});

const view = (over: Partial<Parameters<typeof ExpandedLayRate>[0]> = {}) => {
  const onClose = vi.fn();
  const result = renderWithProviders(<ExpandedLayRate {...props({ onClose, ...over })} />);
  return { ...result, onClose };
};

// jsdom lays nothing out, so the scroll region reports a zero width and
// `dayWindow` answers "fits" — which is the right answer for an unmeasured
// region and the wrong one for this component's interesting states. Both are
// stubbed the way a browser would report them.
function measure({ viewport, scrollLeft }: { viewport: number; scrollLeft: number }) {
  const region = frame().querySelector(".lay-expand-scroll") as HTMLElement;
  Object.defineProperty(region, "clientWidth", { value: viewport, configurable: true });
  Object.defineProperty(region, "scrollLeft", { value: scrollLeft, writable: true, configurable: true });
  fireEvent.scroll(region);
  return region;
}

const cue = (side: "left" | "right") =>
  frame().querySelector(`.scroll-cue.${side}`) as HTMLElement;

// The caption exists twice on purpose: once for the eye, once for the ear.
const caption = () => frame().querySelector(".lay-expand-shown")?.textContent;
const announcement = () => frame().querySelector("[aria-live]")?.textContent;

// A caller that changes `data` for a range of the SAME length, which is the
// only thing that tells `[data]` apart from any key derived from the day count.
function Swapper() {
  const shortRange = (offset: number): DayStripData => ({
    ...data,
    slots: Array.from({ length: 30 }, (_, i) => ({
      kind: "recorded", date: iso(i + offset), eggs: 2000 + i, heightPct: 90,
      weekBreak: i > 0 && i % 7 === 0,
    })),
  });
  const [current, setCurrent] = useState(() => shortRange(0));
  return (
    <>
      <button type="button" onClick={() => setCurrent(shortRange(30))}>swap the range</button>
      <ExpandedLayRate
        data={current} failed={false} label="Eggs per day" title="Eggs per day"
        peak="Peak 2,137" average="Complete-day avg 2,052.4"
        legend={{ complete: "Complete", partial: "Partial", noEntry: "No entry" }}
        tip={(slot) => `${slot.date} readout`}
        scopeName="All flocks" from={iso(0)} to={iso(29)}
        latestDay={iso(29)} range={{ kind: "preset", days: 30 }}
        onRangeChange={() => {}} onClose={() => {}}
      />
    </>
  );
}

beforeEach(() => {
  stubMatchMedia(true);
});

describe("ExpandedLayRate (#941)", () => {
  it("gives the one way out focus on open, and leaves on Escape", () => {
    const { onClose } = view();
    const leave = screen.getByRole("button", { name: "Back to dashboard" });
    expect(document.activeElement).toBe(leave);
    fireEvent.keyDown(screen.getByRole("dialog"), { key: "Escape" });
    expect(onClose).toHaveBeenCalledOnce();
  });

  it("draws the whole range as a map that is a picture, never the only way there", () => {
    view();
    const map = screen.getByRole("img", { name: "Whole range · 90 days" });
    expect(map.querySelectorAll("i")).toHaveLength(DAYS);
    expect(frame().querySelector(".window-box")).not.toBeNull();
  });

  it("lights neither edge cue, and disables the pager, when nothing is hidden", () => {
    // A region as wide as the whole strip, so nothing can be hidden.
    view();
    measure({ viewport: DAYS * (DAY_SLOT_PX + DAY_GAP_PX), scrollLeft: 0 });
    expect(cue("left").hidden).toBe(true);
    expect(cue("right").hidden).toBe(true);
    expect(screen.getByRole("button", { name: "Earlier days" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Later days" })).toBeDisabled();
    expect(caption()).toBe("All 90 days shown");
  });

  it("keeps each edge cue OUTSIDE the scroll region, where an edge stays an edge", () => {
    // Inside it, a cue is positioned against the scrolled CONTENT rather than
    // the region, which drew a pale band down the middle of the chart
    // (#941, lab finding 5).
    view();
    const region = frame().querySelector(".lay-expand-scroll") as HTMLElement;
    for (const side of ["left", "right"] as const) expect(region.contains(cue(side))).toBe(false);
  });

  it("lights only the right cue at the start of a range that is clipped", () => {
    view();
    measure({ viewport: 1080, scrollLeft: 0 });
    expect(cue("left").hidden).toBe(true);
    expect(cue("right").hidden).toBe(false);
    expect(screen.getByRole("button", { name: "Earlier days" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Later days" })).toBeEnabled();
  });

  it("lights both cues mid-range and names the days actually on screen", () => {
    view();
    measure({ viewport: 1080, scrollLeft: 628 });
    expect(cue("left").hidden).toBe(false);
    expect(cue("right").hidden).toBe(false);
    // 24..65 of 90 — the same window `dayWindow` computes from those pixels.
    expect(caption()).toBe(`${shown(24)} – ${shown(65)} shown of 90 days`);
  });

  it("lights only the left cue at the end", () => {
    view();
    measure({ viewport: 1080, scrollLeft: 2336 - 1080 });
    expect(cue("left").hidden).toBe(false);
    expect(cue("right").hidden).toBe(true);
    expect(screen.getByRole("button", { name: "Later days" })).toBeDisabled();
  });

  it("puts the strip's one tab stop on a day in view, not back at day one", () => {
    view();
    measure({ viewport: 1080, scrollLeft: 628 });
    const days = within(screen.getByRole("group", { name: "Eggs per day" })).getAllByRole("button");
    expect(days[0].tabIndex).toBe(-1);
    expect(days[24].tabIndex).toBe(0);
  });

  it("starts the arrow keys from the day in view, so the first press does not teleport the window", () => {
    view();
    measure({ viewport: 1080, scrollLeft: 628 });
    const strip = screen.getByRole("group", { name: "Eggs per day" });
    fireEvent.keyDown(strip, { key: "ArrowRight" });
    expect(document.activeElement).toBe(within(strip).getAllByRole("button")[25]);
  });

  // #958 review round 1 (Claude Fable 5.1), P2: the frame claimed `aria-modal`
  // and enforced none of it. These four are what the claim actually means.
  it("takes the page behind it out of the accessibility tree, and puts it back", () => {
    const { unmount } = view();
    const siblings = [...document.body.children].filter((el) => el !== frame().closest("body > *"));
    expect(siblings.length).toBeGreaterThan(0);
    for (const el of siblings) expect(el.getAttribute("aria-hidden")).toBe("true");
    unmount();
    for (const el of siblings) expect(el.getAttribute("aria-hidden")).toBeNull();
  });

  it("locks the document's scroll while it is open, and restores it", () => {
    const before = document.body.style.overflow;
    const { unmount } = view();
    expect(document.body.style.overflow).toBe("hidden");
    unmount();
    expect(document.body.style.overflow).toBe(before);
  });

  it("leaves on Escape from anywhere inside it, not only from the frame's root", () => {
    const { onClose } = view();
    fireEvent.keyDown(screen.getByRole("button", { name: "Later days" }), { key: "Escape" });
    expect(onClose).toHaveBeenCalledOnce();
  });

  it("hands the pager's focus to its neighbour rather than to the body when it disables", () => {
    view();
    // One page short of the end, so the next press is the one that disables it.
    const region = measure({ viewport: 1080, scrollLeft: 2336 - 1080 * 2 });
    const later = screen.getByRole("button", { name: "Later days" });
    const earlier = screen.getByRole("button", { name: "Earlier days" });
    later.focus();
    fireEvent.click(later);
    expect(document.activeElement).toBe(earlier);
    // The browser's own follow-up to a programmatic scroll, which jsdom does
    // not fire for itself; it is what turns the button disabled.
    fireEvent.scroll(region);
    expect(later).toBeDisabled();
    expect(document.activeElement).toBe(earlier);
  });

  // #958 review round 1, P3: Chrome's native date input steps its focused
  // segment by a month on these two keys.
  it("leaves Page Up and Page Down to a date field the reader is typing in", () => {
    view();
    const region = frame().querySelector(".lay-expand-scroll") as HTMLElement;
    Object.defineProperty(region, "clientWidth", { value: 1080, configurable: true });
    Object.defineProperty(region, "scrollLeft", { value: 500, writable: true, configurable: true });
    const field = document.createElement("input");
    field.type = "date";
    frame().append(field);
    const handled = fireEvent.keyDown(field, { key: "PageDown", bubbles: true });
    expect(handled).toBe(true);           // nothing called preventDefault
    expect(region.scrollLeft).toBe(500);  // and the window did not move
  });

  // #958 review round 1, P3: the visible caption must not lag the chart, and
  // the announcement must not fire once per day crossed on a drag. Round 2,
  // P3: nor may it speak for a window nobody has moved — the first settled
  // reading of a range is the baseline, not news.
  it("says nothing about a window nobody has moved, then announces one that settles", () => {
    vi.useFakeTimers();
    try {
      view();
      measure({ viewport: 1080, scrollLeft: 0 });
      act(() => { vi.advanceTimersByTime(600); });
      expect(caption()).toBe(`${shown(0)} – ${shown(41)} shown of 90 days`);
      expect(announcement()).toBe("");

      measure({ viewport: 1080, scrollLeft: 628 });
      expect(caption()).toBe(`${shown(24)} – ${shown(65)} shown of 90 days`);
      expect(announcement()).toBe("");

      act(() => { vi.advanceTimersByTime(600); });
      expect(announcement()).toBe(caption());
    } finally {
      vi.useRealTimers();
    }
  });

  // #958 review round 2, P3: `overscroll-behavior` on a region that does not
  // overflow is never consulted, and at 390 the column is sized to fit — so
  // the rule on `.lay-expand` is inert exactly where the gesture lives. The
  // root element is in the scroll chain whatever the panel's height.
  it("holds off the document's own overscroll while it is open, and gives it back", () => {
    const root = document.documentElement;
    root.style.overscrollBehaviorY = "auto";
    const { unmount } = view();
    expect(root.style.overscrollBehaviorY).toBe("contain");
    unmount();
    expect(root.style.overscrollBehaviorY).toBe("auto");
  });

  // #958 review round 2, P2: a MUI `Modal` takes the page out of the
  // accessibility tree the same way this app's own `Dialog` does, so the PWA
  // update banner cannot speak from behind it. Uncounted, #485 records no debt
  // and replays nothing, and a screen-reader user is never told a new version
  // arrived.
  it("counts as an open dialog while it is up, so a silenced page is known to be silenced", () => {
    expect(anyDialogOpen()).toBe(false);
    const { unmount } = view();
    expect(anyDialogOpen()).toBe(true);
    unmount();
    expect(anyDialogOpen()).toBe(false);
  });

  it("replays an update banner that arrived while it was covering the page", async () => {
    const banner = "A new version is available";
    function Shell({ open }: { open: boolean }) {
      const spoken = useMissedAnnouncement(banner);
      return (
        <>
          <span data-testid="replay">{spoken}</span>
          {open ? <ExpandedLayRate {...(props())} /> : null}
        </>
      );
    }
    const { rerender } = renderWithProviders(<Shell open />);
    await act(async () => { await Promise.resolve(); });
    // The page is `aria-hidden` behind the chart, so the banner's own region
    // said nothing and the debt is held rather than spoken.
    expect(screen.getByTestId("replay")).toHaveTextContent("");

    rerender(<Shell open={false} />);
    await act(async () => { await Promise.resolve(); });
    expect(screen.getByTestId("replay")).toHaveTextContent(banner);
  });

  // #958 review round 1, P3: the scroll-to-the-newest-day effect used to be
  // keyed on `sync`, whose identity followed the gap, which follows the
  // breakpoint. It answers to the REPORT now — which is what "a new range
  // opened" means, and the only thing that should re-anchor the window.
  it("keeps the reader's place when the viewport crosses the phone breakpoint", () => {
    const media = stubMatchMedia(true);
    view();
    const region = measure({ viewport: 1080, scrollLeft: 628 });
    act(() => { media.triggerChange(false); });
    expect(region.scrollLeft).toBe(628);
  });

  it("re-anchors on the newest day when a new range of the same length arrives", () => {
    // 30 days then a different 30 days: the day COUNT is unchanged, so an
    // effect keyed on anything derived from it would leave the reader looking
    // at the wrong end of a range they did not ask for.
    renderWithProviders(<Swapper />);
    const region = measure({ viewport: 1080, scrollLeft: 400 });
    expect(region.scrollLeft).toBe(400);
    // By text, not by role: the open chart has made everything behind it
    // `aria-hidden`, so a role query cannot see this caller's own button.
    fireEvent.click(screen.getByText("swap the range"));
    expect(region.scrollLeft).toBe(0); // jsdom lays nothing out, so scrollWidth is 0
  });

  it("shows its own read failing without blaming the dashboard behind it", () => {
    view({ data: null, failed: true });
    expect(screen.getByRole("alert")).toHaveTextContent("Could not load.");
  });
});
