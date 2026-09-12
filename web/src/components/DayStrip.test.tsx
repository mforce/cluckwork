// web/src/components/DayStrip.test.tsx
import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DayStrip } from "./DayStrip";
import type { DayStripData, DayStripSlot } from "../lib/dashboard";

const rec = (date: string, eggs: number, heightPct: number, weekBreak = false): DayStripSlot =>
  ({ kind: "recorded", date, eggs, heightPct, weekBreak });
const gap = (date: string, weekBreak = false): DayStripSlot =>
  ({ kind: "unrecorded", date, expectedFlocks: 3, weekBreak });
const part = (date: string, eggs: number, heightPct: number): DayStripSlot =>
  ({ kind: "partial", date, eggs, heightPct, filedFlocks: 1, expectedFlocks: 3, weekBreak: false });

// Four days, one per state: one that laid, one that laid nothing and was
// recorded, one only some houses reported, one nobody recorded. The last three
// all used to render as the same empty slot.
const data: DayStripData = {
  slots: [rec("2026-07-01", 10, 100), rec("2026-07-02", 0, 2), part("2026-07-04", 6, 60), gap("2026-07-03", true)],
  max: 10, average: 5, averagePct: 50, complete: 2, partial: 1, unrecorded: 1,
};

const tip = (s: DayStripSlot) => {
  switch (s.kind) {
    case "none": return `${s.date} – no flocks`;
    case "unrecorded": return `${s.date} – no entry`;
    case "partial": return `${s.date} – ${s.eggs} eggs, 1 of 3 flocks`;
    case "recorded": return `${s.date} – ${s.eggs} eggs`;
  }
};

const strip = (d: DayStripData = data, label = "Eggs per day") => (
  <DayStrip
    data={d} label={label} title="Eggs per day" peak="Peak 10" average="Avg 5"
    tip={tip} from="1 Jul" to="3 Jul"
  />
);

describe("DayStrip (#654, #777, #780)", () => {
  it("draws a slot for every day, and a bar only for a day somebody recorded", () => {
    const { container } = render(strip());
    expect(container.querySelectorAll(".daystrip > .day")).toHaveLength(4);
    const bars = Array.from(container.querySelectorAll(".day > i")).map((b) => (b as HTMLElement).style.height);
    // The recorded zero keeps its 2% stub and the partial day draws its floor;
    // the unrecorded day has no bar at all.
    expect(bars).toEqual(["100%", "2%", "60%"]);
  });

  it("places the average line at its share of the peak, and omits it when there is none", () => {
    const { container } = render(strip());
    expect((container.querySelector(".avgline") as HTMLElement).style.bottom).toBe("50%");
    const { container: none } = render(strip({ ...data, average: null, averagePct: null }));
    expect(none.querySelector(".avgline")).toBeNull();
  });

  it("marks the week boundary on exactly the slot that carries it", () => {
    const { container } = render(strip());
    const marked = Array.from(container.querySelectorAll(".daystrip > .day"))
      .map((d) => d.classList.contains("day-week"));
    expect(marked).toEqual([false, false, false, true]);
  });

  it("names the group, the scale and both ends of the window, and follows a re-render", () => {
    const { rerender } = render(strip());
    expect(screen.getByRole("group", { name: "Eggs per day" })).toBeInTheDocument();
    expect(screen.getByText("Peak 10")).toBeInTheDocument();
    expect(screen.getByText("Avg 5")).toBeInTheDocument();
    expect(screen.getByText("1 Jul")).toBeInTheDocument();

    rerender(strip({ slots: [gap("2026-08-01")], max: null, average: null, averagePct: null, complete: 0, partial: 0, unrecorded: 1 }, "Flat"));
    expect(screen.getByRole("group", { name: "Flat" })).toBeInTheDocument();
    expect(screen.queryByRole("group", { name: "Eggs per day" })).not.toBeInTheDocument();
  });

  // Every day carries its own sentence as the slot's accessible name, so the
  // readout is not pointer-only — the figure appears nowhere else on the panel.
  it("gives every slot its own name, saying 'no entry' rather than a count of zero", () => {
    render(strip());
    expect(screen.getByRole("button", { name: "2026-07-01 – 10 eggs" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "2026-07-02 – 0 eggs" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "2026-07-03 – no entry" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "2026-07-04 – 6 eggs, 1 of 3 flocks" })).toBeInTheDocument();
  });

  // A partly recorded day is marked in the markup, not only in its name — the
  // bar is a floor and a solid bar would assert a drop nobody recorded.
  it("marks each slot with its own state so the bar can say it is a floor", () => {
    const { container } = render(strip());
    expect(Array.from(container.querySelectorAll(".daystrip > .day")).map((d) => d.className)).toEqual([
      "day day-recorded", "day day-recorded", "day day-partial", "day day-unrecorded day-week",
    ]);
  });

  it("shows that day's readout on hover and marks the slot it describes", async () => {
    const user = userEvent.setup();
    const { container } = render(strip());
    expect(container.querySelector(".tip")).toBeNull();

    const days = Array.from(container.querySelectorAll(".daystrip > .day"));
    await user.hover(days[3]);
    expect(container.querySelector(".tip")!.textContent).toBe("2026-07-03 – no entry");
    // The SLOT is marked, not just the bar — the day it names has no bar.
    expect(days.map((d) => d.classList.contains("on"))).toEqual([false, false, false, true]);

    await user.hover(days[0]);
    expect(container.querySelector(".tip")!.textContent).toBe("2026-07-01 – 10 eggs");
    expect(days.map((d) => d.classList.contains("on"))).toEqual([true, false, false, false]);
  });

  // One tab stop for the whole strip, then the arrow keys along it. Fourteen
  // consecutive tab stops past a dashboard panel is a keyboard obstacle, not
  // keyboard support.
  it("takes one tab stop and moves along the days with the arrow keys", async () => {
    const user = userEvent.setup();
    const { container } = render(strip());
    const days = () => Array.from(container.querySelectorAll(".daystrip > .day"));
    expect(days().map((d) => d.getAttribute("tabindex"))).toEqual(["0", "-1", "-1", "-1"]);

    await user.tab();
    expect(container.querySelector(".tip")!.textContent).toBe("2026-07-01 – 10 eggs");
    await user.keyboard("{ArrowRight}");
    expect(container.querySelector(".tip")!.textContent).toBe("2026-07-02 – 0 eggs");
    // The stop follows the selection, so Tab re-enters where the reader left.
    expect(days().map((d) => d.getAttribute("tabindex"))).toEqual(["-1", "0", "-1", "-1"]);
    await user.keyboard("{End}");
    expect(container.querySelector(".tip")!.textContent).toBe("2026-07-03 – no entry");
    // Clamped, never wrapped off the end of the array.
    await user.keyboard("{ArrowRight}");
    expect(container.querySelector(".tip")!.textContent).toBe("2026-07-03 – no entry");
    await user.keyboard("{Home}");
    expect(container.querySelector(".tip")!.textContent).toBe("2026-07-01 – 10 eggs");

    // Tab leaves the strip in one press rather than walking every day.
    await user.tab();
    expect(days().some((d) => d === document.activeElement)).toBe(false);
  });

  // A keyboard user focuses a day, then the pointer wanders across the strip and
  // off it. Clearing on mouse-leave took the readout and the ring away from a
  // day that still held focus, with no blur to explain it.
  it("keeps the focused day's readout when the pointer leaves the strip", async () => {
    const user = userEvent.setup();
    const { container } = render(strip());
    const days = () => Array.from(container.querySelectorAll(".daystrip > .day"));

    await user.tab();
    await user.keyboard("{ArrowRight}");
    expect(container.querySelector(".tip")!.textContent).toBe("2026-07-02 – 0 eggs");

    await user.hover(days()[3]);
    expect(container.querySelector(".tip")!.textContent).toBe("2026-07-03 – no entry");

    await user.unhover(days()[3]);
    // Back to where the keyboard is, not gone.
    expect(container.querySelector(".tip")!.textContent).toBe("2026-07-02 – 0 eggs");
    expect(days().map((d) => d.classList.contains("on"))).toEqual([false, true, false, false]);

    // With nothing focused, leaving the strip still closes it.
    await user.tab();
    await user.hover(days()[0]);
    await user.unhover(days()[0]);
    expect(container.querySelector(".tip")).toBeNull();
  });

  // The selection is a roving one, not a toggle: `aria-pressed` said "this
  // button is pressed in" about a chart slot. Nothing read the attribute, so
  // hardcoding it to false left the whole suite green.
  it("marks the selected slot as current, and only that slot", async () => {
    const user = userEvent.setup();
    const { container } = render(strip());
    const days = () => Array.from(container.querySelectorAll(".daystrip > .day"));
    expect(days().map((d) => d.getAttribute("aria-current"))).toEqual(["false", "false", "false", "false"]);
    await user.hover(days()[2]);
    expect(days().map((d) => d.getAttribute("aria-current"))).toEqual(["false", "false", "true", "false"]);
    expect(container.querySelector("[aria-pressed]")).toBeNull();
  });

  // The readout is not a live region and must not be: the selected slot's own
  // accessible name is this exact sentence, so a live region announced it twice.
  it("hides the readout from assistive tech, leaving the slot's name to say it", () => {
    const { container } = render(strip());
    expect(container.querySelector(".tipdock")).toHaveAttribute("aria-hidden", "true");
    expect(container.querySelector("[role=status]")).toBeNull();
  });

  // The selection is held by DATE. Holding a snapshot of the slot left the
  // readout showing a figure the same day's accessible name had already replaced.
  it("follows a data change under an open readout instead of showing a stale figure", async () => {
    const user = userEvent.setup();
    const { container, rerender } = render(strip());
    await user.hover(container.querySelectorAll(".daystrip > .day")[0]);
    expect(container.querySelector(".tip")!.textContent).toBe("2026-07-01 – 10 eggs");

    rerender(strip({ ...data, slots: [rec("2026-07-01", 999, 100), ...data.slots.slice(1)] }));
    expect(container.querySelector(".tip")!.textContent).toBe("2026-07-01 – 999 eggs");
    expect(screen.getByRole("button", { name: "2026-07-01 – 999 eggs" })).toBeInTheDocument();
  });

  // A day that leaves the window takes its readout with it, rather than leaving
  // a box pointing at a slot that is no longer drawn.
  it("closes the readout when the selected day drops out of the window", async () => {
    const user = userEvent.setup();
    const { container, rerender } = render(strip());
    await user.hover(container.querySelectorAll(".daystrip > .day")[0]);
    expect(container.querySelector(".tip")).not.toBeNull();
    rerender(strip({ ...data, slots: data.slots.slice(1) }));
    expect(container.querySelector(".tip")).toBeNull();
  });

  // The dock is reserved whether or not a day is selected, so the box appearing
  // reflows nothing below it.
  it("keeps the readout's row present with nothing selected", () => {
    const { container } = render(strip());
    expect(container.querySelector(".tipdock")).toBeInTheDocument();
  });
});
