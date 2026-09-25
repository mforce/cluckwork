// web/src/components/ExpandedLayRate.test.tsx
import { describe, it, expect, vi, beforeEach } from "vitest";
import { fireEvent, screen, within } from "@testing-library/react";
import { renderWithProviders } from "../test/renderWithProviders";
import { daysBefore } from "../lib/dates";
import { DEFAULT_LOCALE, formatDate } from "../lib/format";
import { stubMatchMedia } from "../test/matchMedia";
import { ExpandedLayRate } from "./ExpandedLayRate";
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

const view = (over: Partial<Parameters<typeof ExpandedLayRate>[0]> = {}) => {
  const onClose = vi.fn();
  const result = renderWithProviders(
    <ExpandedLayRate
      data={data} failed={false} label="Eggs per day" title="Eggs per day · complete-day scale"
      peak="Peak 2,137" average="Complete-day avg 2,052.4"
      legend={{ complete: "Complete", partial: "Partial", noEntry: "No entry" }}
      tip={(slot) => `${slot.date} readout`}
      scopeName="All flocks" from={slots[0].date} to={slots[DAYS - 1].date}
      latestDay={slots[DAYS - 1].date} range={{ kind: "preset", days: 30 }}
      onRangeChange={() => {}} onClose={onClose} {...over}
    />,
  );
  return { ...result, onClose };
};

// jsdom lays nothing out, so the scroll region reports a zero width and
// `dayWindow` answers "fits" — which is the right answer for an unmeasured
// region and the wrong one for this component's interesting states. Both are
// stubbed the way a browser would report them.
function measure(container: HTMLElement, { viewport, scrollLeft }: { viewport: number; scrollLeft: number }) {
  const region = container.querySelector(".lay-expand-scroll") as HTMLElement;
  Object.defineProperty(region, "clientWidth", { value: viewport, configurable: true });
  Object.defineProperty(region, "scrollLeft", { value: scrollLeft, writable: true, configurable: true });
  fireEvent.scroll(region);
  return region;
}

const cue = (container: HTMLElement, side: "left" | "right") =>
  container.querySelector(`.scroll-cue.${side}`) as HTMLElement;

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
    const { container } = view();
    const map = screen.getByRole("img", { name: "Whole range · 90 days" });
    expect(map.querySelectorAll("i")).toHaveLength(DAYS);
    expect(container.querySelector(".window-box")).not.toBeNull();
  });

  it("lights neither edge cue, and disables the pager, when nothing is hidden", () => {
    // 41 days at a 22px slot and a 4px gap is 1,062px, which this region fits.
    const { container } = view();
    measure(container, { viewport: DAYS * (DAY_SLOT_PX + DAY_GAP_PX), scrollLeft: 0 });
    expect(cue(container, "left").hidden).toBe(true);
    expect(cue(container, "right").hidden).toBe(true);
    expect(screen.getByRole("button", { name: "Earlier days" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Later days" })).toBeDisabled();
    expect(screen.getByText("All 90 days shown")).toBeInTheDocument();
  });

  it("keeps each edge cue OUTSIDE the scroll region, where an edge stays an edge", () => {
    // Inside it, a cue is positioned against the scrolled CONTENT rather than
    // the region, which drew a pale band down the middle of the chart
    // (#941, lab finding 5).
    const { container } = view();
    const region = container.querySelector(".lay-expand-scroll") as HTMLElement;
    for (const side of ["left", "right"] as const) expect(region.contains(cue(container, side))).toBe(false);
  });

  it("lights only the right cue at the start of a range that is clipped", () => {
    const { container } = view();
    measure(container, { viewport: 1080, scrollLeft: 0 });
    expect(cue(container, "left").hidden).toBe(true);
    expect(cue(container, "right").hidden).toBe(false);
    expect(screen.getByRole("button", { name: "Earlier days" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Later days" })).toBeEnabled();
  });

  it("lights both cues mid-range and names the days actually on screen", () => {
    const { container } = view();
    measure(container, { viewport: 1080, scrollLeft: 628 });
    expect(cue(container, "left").hidden).toBe(false);
    expect(cue(container, "right").hidden).toBe(false);
    // 24..65 of 90 — the same window `dayWindow` computes from those pixels.
    expect(screen.getByText(`${shown(24)} – ${shown(65)} shown of 90 days`)).toBeInTheDocument();
  });

  it("lights only the left cue at the end", () => {
    const { container } = view();
    measure(container, { viewport: 1080, scrollLeft: 2336 - 1080 });
    expect(cue(container, "left").hidden).toBe(false);
    expect(cue(container, "right").hidden).toBe(true);
    expect(screen.getByRole("button", { name: "Later days" })).toBeDisabled();
  });

  it("puts the strip's one tab stop on a day in view, not back at day one", () => {
    const { container } = view();
    measure(container, { viewport: 1080, scrollLeft: 628 });
    const days = within(screen.getByRole("group", { name: "Eggs per day" })).getAllByRole("button");
    expect(days[0].tabIndex).toBe(-1);
    expect(days[24].tabIndex).toBe(0);
  });

  it("starts the arrow keys from the day in view, so the first press does not teleport the window", () => {
    const { container } = view();
    measure(container, { viewport: 1080, scrollLeft: 628 });
    const strip = screen.getByRole("group", { name: "Eggs per day" });
    fireEvent.keyDown(strip, { key: "ArrowRight" });
    expect(document.activeElement).toBe(within(strip).getAllByRole("button")[25]);
  });

  it("shows its own read failing without blaming the dashboard behind it", () => {
    view({ data: null, failed: true });
    expect(screen.getByRole("alert")).toHaveTextContent("Could not load.");
  });
});
