// web/src/components/useDayStrip.ts
//
// #654 → #777 → #780 → #941 — the day strip's behaviour, shared by the
// Dashboard card (DayStrip) and the expanded chart (ExpandedLayRate). One tab
// stop, arrow keys along the days, a measured readout clamp, and the pointer
// handing the readout back to the keyboard on the way out.
//
// It is a hook rather than a second component because the two surfaces frame
// the same strip differently: the card draws it in place, the expanded view
// draws it inside a horizontal scroll region beside a fixed axis gutter. What
// must not differ is what a key press does.
import { useLayoutEffect, useRef, useState } from "react";
import type { RefObject } from "react";
import type { DayStripSlot } from "../lib/dashboard";

export interface DayStripScroll {
  // The first day actually on screen. The single tab stop lands here, and so
  // does the arrow keys' fallback with nothing selected yet — rendered on day
  // 0 instead, merely tabbing into a window scrolled to September made the
  // browser scroll June back into view, losing the reader's place before a key
  // was pressed (#941, lab finding 2).
  firstVisible: number;
  scrollerRef: RefObject<HTMLDivElement | null>;
  // The fixed axis gutter to the scroller's left. The readout dock spans both,
  // so a slot's own offset has to be shifted into the dock's coordinates.
  gutterRef: RefObject<HTMLDivElement | null>;
}

export function useDayStrip(
  slots: DayStripSlot[],
  tip: (slot: DayStripSlot) => string,
  scroll?: DayStripScroll,
) {
  // The selected day is held by DATE, never as a snapshot of the slot. Holding
  // the object meant a refetch left the readout showing a figure the same day's
  // own accessible name had already replaced, and an index could point outside
  // a shortened array.
  const [activeDate, setActiveDate] = useState<string | null>(null);
  // Where the keyboard is, which is NOT the same as what is selected: a pointer
  // crossing the strip and leaving clears the selection without moving DOM
  // focus, and deriving the tab stop from the selection then snapped it back to
  // day 1 while focus sat on day 3 — the next ArrowRight moved backwards.
  const [focusDate, setFocusDate] = useState<string | null>(null);
  const dockRef = useRef<HTMLDivElement>(null);
  const tipRef = useRef<HTMLSpanElement>(null);
  const stripRef = useRef<HTMLDivElement>(null);

  const activeIndex = slots.findIndex((s) => s.date === activeDate);
  const active = activeIndex === -1 ? null : slots[activeIndex];
  const focusIndex = slots.findIndex((s) => s.date === focusDate);
  // Tab lands where the keyboard last was, else on the first day in view —
  // never on nothing, which is what an all-`-1` strip would give.
  const stopIndex = focusIndex === -1 ? Math.min(scroll?.firstVisible ?? 0, Math.max(0, slots.length - 1)) : focusIndex;

  // Clamp the box inside the panel against MEASURED widths. It sizes to its own
  // text, which runs roughly 30% longer in tl than in en (#688), so nothing
  // here may assume a width. The arrow is anchored to the slot element instead
  // (.day.on::after), never positioned by index arithmetic: the slots carry a
  // gap, so (i + 0.5) / n is not where a slot is.
  //
  // Written as a TRANSFORM, against a box the stylesheet pins at `left: 0`.
  // Writing `left` here resized the box it was about to measure, so the
  // placement and the width defined each other and one of them was always a
  // step behind (#962 — the reasoning is on `.tip` in styles.css).
  const placeReadout = () => {
    const dock = dockRef.current;
    const box = tipRef.current;
    // Measured from the SELECTED slot's own element, never from a remembered
    // number. A stored centre went stale: focus an early day, hover a later
    // one, move the pointer off the strip, and the restore put the box over the
    // hovered day while the arrow and ring stayed on the focused one.
    const slot = stripRef.current?.children[activeIndex];
    if (dock === null || box === null || !(slot instanceof HTMLElement)) return;
    // In the expanded view the strip is inset by the axis gutter and displaced
    // by the scroll, so the slot's own offset is not yet a dock coordinate.
    const shift = (scroll?.gutterRef.current?.offsetWidth ?? 0) - (scroll?.scrollerRef.current?.scrollLeft ?? 0);
    const centre = slot.offsetLeft + slot.offsetWidth / 2 + shift;
    // `.tip` carries `max-width: 100%` of the dock, so a box wider than its
    // row should be impossible — but #941 overrode two other declarations on
    // this exact selector from a panel's `sx`, so the floor stays and a box
    // that outgrows its dock lands at 0 rather than at a negative offset.
    const slack = Math.max(0, dock.offsetWidth - box.offsetWidth);
    const x = Math.min(Math.max(centre - box.offsetWidth / 2, 0), slack);
    box.style.transform = `translateX(${x}px)`;
  };

  // `tip(active)` is in the deps because the farm locale resolves after mount,
  // which changes the text's width without changing which day is selected.
  //
  // The observer is what makes the placement follow the box's CURRENT size
  // rather than its size when a day was chosen: a wrap appearing, a late
  // webfont and a panel resize all change the width with no React render to
  // hang an effect on. It is loop-safe only because the placement is a
  // transform, which cannot change what it measures.
  useLayoutEffect(() => {
    placeReadout();
    const box = tipRef.current;
    const dock = dockRef.current;
    if (box === null || dock === null) return;
    const observer = new ResizeObserver(placeReadout);
    observer.observe(box);
    observer.observe(dock);
    return () => observer.disconnect();
  }, [activeIndex, tip]);

  const select = (slot: DayStripSlot, keyboard = false) => {
    setActiveDate(slot.date);
    if (keyboard) setFocusDate(slot.date);
  };

  // A day walked onto by the arrow keys must not stay off screen. 24px of lead
  // so the slot arrives clear of the edge cue rather than under it.
  const reveal = (slot: HTMLElement) => {
    const region = scroll?.scrollerRef.current;
    if (!region) return;
    const right = slot.offsetLeft + slot.offsetWidth;
    if (slot.offsetLeft < region.scrollLeft + 8) region.scrollLeft = Math.max(0, slot.offsetLeft - 24);
    else if (right > region.scrollLeft + region.clientWidth - 8) region.scrollLeft = right - region.clientWidth + 24;
  };

  const handleKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    const step = e.key === "ArrowRight" ? 1 : e.key === "ArrowLeft" ? -1
      : e.key === "Home" ? -slots.length : e.key === "End" ? slots.length : 0;
    if (step === 0) return;
    e.preventDefault();
    const next = Math.min(slots.length - 1, Math.max(0, stopIndex + step));
    const el = stripRef.current?.children[next];
    if (el instanceof HTMLElement) { reveal(el); el.focus(); }
  };

  // The pointer leaving hands the readout back to the KEYBOARD, if the keyboard
  // still has it. Clearing unconditionally took the readout and the ring off a
  // day that still held focus, with no blur to explain it.
  const handleMouseLeave = () => setActiveDate(
    stripRef.current?.contains(document.activeElement) === true ? focusDate : null,
  );

  return {
    dockRef, tipRef, stripRef,
    activeDate, active, stopIndex,
    select, handleKeyDown, handleMouseLeave, placeReadout,
  };
}
