// web/src/components/ExpandedLayRate.tsx
//
// #941 — the Lay rate chart at full size: an overview map of the whole range
// above a daily window that scrolls. Concept C of the #941 mockup lab, chosen
// by the owner on 2026-09-24 and committed under docs/designs/941-expand-chart/.
//
// The frame is the post-login splash's (BrandSplash.tsx, .brand-splash-backdrop):
// a fixed full-viewport backdrop, the content centred on both axes, one clear
// way out that takes focus on open, and a fade only under
// prefers-reduced-motion: no-preference. Two departures, both the owner's: the
// column is wider than the splash's 640px because the subject is 90 bars
// rather than one image, and it follows the app theme instead of forcing a
// dark lightbox.
//
// The sync between the map and the window has ONE writer each way. Scrolling
// writes the box's style; pressing the map writes `scrollLeft`. A programmatic
// `scrollLeft` fires `scroll`, which repaints the box, and the box never
// writes back — so the chain terminates after one hop and needs no guard flag.
// Give the box its own index state and that loop comes back.
import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, LinearProgress, Modal, TextField, useMediaQuery } from "@mui/material";
import { ChevronLeft, ChevronRight } from "lucide-react";
import { DayLegend, DayReadout } from "./DayStrip";
import { DaySlots } from "./DaySlots";
import { useDayStrip } from "./useDayStrip";
import { FilterDateField } from "./FilterBar";
import { useFormat } from "../farm/useFormat";
import { MD_UP_QUERY } from "../lib/breakpoints";
import { DAY_GAP_PX, DAY_SLOT_PX, dayWindow, stripWidth } from "../lib/dayWindow";
import type { DayWindow, StripMetrics } from "../lib/dayWindow";
import {
  EXPANDED_RANGE_PRESETS, MAX_EXPANDED_RANGE_DAYS, customRangeError,
} from "../lib/layRateRange";
import type { CustomRangeError, LayRateRange } from "../lib/layRateRange";
import type { DayStripData, DayStripSlot } from "../lib/dashboard";

// Every seventh day carries a date on the rule, counted back from the newest
// day so the label the reader looks at first sits on the anchor of the chart.
const TICK_DAYS = 7;
// A label within this many slots of the left edge is left-aligned instead of
// centred, so it cannot paint outside the strip it belongs to.
const EDGE_TICK_SLOTS = 3;
// How long the window has to settle before the visible span is announced. A
// drag crosses a day boundary every 26px, so an undebounced live region would
// speak forty times on one sweep of a quarter.
const ANNOUNCE_SETTLE_MS = 500;

export function ExpandedLayRate({
  data, failed, label, title, peak, average, legend, tip,
  scopeName, from, to, latestDay, range, onRangeChange, onClose,
}: {
  data: DayStripData | null;
  failed: boolean;
  label: string;
  title: string;
  peak: string;
  average: string;
  legend: { complete: string; partial: string; noEntry: string };
  tip: (slot: DayStripSlot) => string;
  scopeName: string;
  from: string;
  to: string;
  latestDay: string;
  range: LayRateRange;
  onRangeChange: (next: LayRateRange) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation("dashboard");
  const fmt = useFormat();
  const titleId = useId();
  const isDesktop = useMediaQuery(MD_UP_QUERY);
  const scrollerRef = useRef<HTMLDivElement>(null);
  const gutterRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<HTMLDivElement>(null);
  const boxRef = useRef<HTMLSpanElement>(null);
  const prevPageRef = useRef<HTMLButtonElement>(null);
  const nextPageRef = useRef<HTMLButtonElement>(null);

  const slots = data?.slots ?? [];
  const days = slots.length;
  const [view, setView] = useState<DayWindow>(
    () => dayWindow({ days, slot: DAY_SLOT_PX, gap: DAY_GAP_PX, viewport: 0 }, 0),
  );
  const strip = useDayStrip(slots, tip, { firstVisible: view.firstVisible, scrollerRef, gutterRef });

  // The gap is read back off the STRIP, so the stylesheet is the only thing
  // that decides it. `MD_UP_QUERY` is `(min-width: 900px)` and the phone rule
  // is `(max-width: 900px)`, so both match at exactly 900 — there a JS-side
  // 4px against a laid-out 2px put `stripWidth` 178px out over 90 days. The
  // fallback covers jsdom, which loads no stylesheet to read.
  const metricsOf = useCallback((region: HTMLElement | null): StripMetrics => {
    const node = strip.stripRef.current;
    const measured = Number.parseFloat(node === null ? "" : getComputedStyle(node).columnGap);
    return {
      days,
      slot: DAY_SLOT_PX,
      gap: Number.isFinite(measured) ? measured : DAY_GAP_PX,
      viewport: region?.clientWidth ?? 0,
    };
  }, [days, strip.stripRef]);

  // The box is written to the DOM rather than rendered from state: its left
  // and width change on every scroll frame, and a 90-button strip re-rendered
  // that often is the difference between a scroll that glides and one that
  // stutters. Only the DISCRETE answers — which days are on screen, which
  // edges hide something — go back into React.
  const sync = useCallback(() => {
    const region = scrollerRef.current;
    const next = dayWindow(metricsOf(region), region?.scrollLeft ?? 0);
    if (boxRef.current !== null) {
      boxRef.current.style.left = `${next.boxLeftPct}%`;
      boxRef.current.style.width = `${next.boxWidthPct}%`;
    }
    setView((prev) => (prev.fits === next.fits && prev.firstVisible === next.firstVisible
      && prev.lastVisible === next.lastVisible && prev.atStart === next.atStart
      && prev.atEnd === next.atEnd) ? prev : next);
  }, [metricsOf]);

  // The newest day sits at the right edge, as it does on the card, so the view
  // opens on the days the farm just filed. Keyed on the REPORT, which is what
  // "a new range opened" means: keyed on anything derived from the day count,
  // a new range of the same length leaves the reader at the wrong end of it.
  useLayoutEffect(() => {
    const region = scrollerRef.current;
    if (region !== null) region.scrollLeft = region.scrollWidth;
    sync();
  }, [data]);

  useEffect(() => {
    const region = scrollerRef.current;
    if (region === null) return;
    const observer = new ResizeObserver(() => sync());
    observer.observe(region);
    return () => observer.disconnect();
  }, [sync]);

  const page = (direction: number) => {
    const region = scrollerRef.current;
    if (region !== null) region.scrollLeft += direction * region.clientWidth;
  };

  // A pager button that disables while it holds focus drops focus to <body>,
  // and the next Tab lands wherever the browser's starting-point rule chooses.
  // `scrollLeft` reads back clamped in the same tick, so the edge is known
  // before the disabled attribute commits.
  const pageFromButton = (direction: number) => {
    page(direction);
    const region = scrollerRef.current;
    if (region === null) return;
    const next = dayWindow(metricsOf(region), region.scrollLeft);
    if (direction > 0 && next.atEnd) prevPageRef.current?.focus();
    if (direction < 0 && next.atStart) nextPageRef.current?.focus();
  };

  // A press on the map centres the window on the pressed point; a drag repeats
  // it under pointer capture, so the sweep survives the cursor leaving a 42px
  // strip. At 390 the box is about 44px wide — a position readout and a tap
  // target, not a drag handle — so the phone gets the jump and nothing else,
  // and the gesture that moves the window is a swipe on the window itself.
  const jumpTo = (clientX: number) => {
    const map = mapRef.current;
    const region = scrollerRef.current;
    if (map === null || region === null) return;
    const box = map.getBoundingClientRect();
    const fraction = Math.min(1, Math.max(0, (clientX - box.left) / box.width));
    region.scrollLeft = fraction * stripWidth(metricsOf(region)) - region.clientWidth / 2;
  };

  const onMapPointerDown = (e: React.PointerEvent<HTMLDivElement>) => {
    if (view.fits) return;
    e.preventDefault();
    jumpTo(e.clientX);
    if (!isDesktop) return;
    const map = e.currentTarget;
    map.setPointerCapture(e.pointerId);
    const move = (m: PointerEvent) => jumpTo(m.clientX);
    const done = () => {
      map.removeEventListener("pointermove", move);
      map.removeEventListener("pointerup", done);
      map.removeEventListener("pointercancel", done);
      if (map.hasPointerCapture(e.pointerId)) map.releasePointerCapture(e.pointerId);
    };
    map.addEventListener("pointermove", move);
    map.addEventListener("pointerup", done);
    map.addEventListener("pointercancel", done);
  };

  // Page Up and Page Down are the keyboard's pager and the keyboard's swipe:
  // they move the WINDOW and leave the selection where it is. Tab containment
  // and Escape belong to `Modal` below, not to this handler.
  const onKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    if (e.key !== "PageUp" && e.key !== "PageDown") return;
    // Chrome's native date input steps its focused segment by a month on these
    // two keys; swallowing them everywhere took that away inside the form.
    if (e.target instanceof HTMLInputElement || e.target instanceof HTMLSelectElement) return;
    e.preventDefault();
    page(e.key === "PageDown" ? 1 : -1);
  };

  const rangeSpan = t("rangeSpan", { from: fmt.date(from), to: fmt.date(to) });
  const shownNote = view.fits || days === 0
    ? t("expandAllShown", { count: days, days: fmt.count(days) })
    : t("expandShown", {
        from: fmt.date(slots[view.firstVisible].date),
        to: fmt.date(slots[view.lastVisible].date),
        count: days, days: fmt.count(days),
      });
  const [announced, setAnnounced] = useState(shownNote);
  useEffect(() => {
    const timer = setTimeout(() => setAnnounced(shownNote), ANNOUNCE_SETTLE_MS);
    return () => clearTimeout(timer);
  }, [shownNote]);

  return (
    // `Modal` for the `ModalManager` every Dialog here already depends on:
    // siblings go `aria-hidden`, the body's scroll is locked and restored,
    // Escape closes, Tab is contained however focus got where it is, and the
    // frame takes `theme.zIndex.modal` instead of a literal in the stylesheet.
    // `hideBackdrop` because the opaque `.lay-expand-backdrop` IS the backdrop;
    // `disableRestoreFocus` because the caller restores focus itself, and two
    // actors doing it is the conflict #483 is named for.
    <Modal open onClose={onClose} hideBackdrop disableRestoreFocus>
      <div
        className="lay-expand-backdrop" role="dialog" aria-modal="true" aria-labelledby={titleId}
        onKeyDown={onKeyDown}
      >
      <div className="lay-expand">
        <div className="lay-expand-panel">
          <div className="lay-expand-head">
            <div>
              <h2 className="lay-expand-title" id={titleId}>{t("layRateTitle")}</h2>
              <p className="lay-expand-context">
                {t("layRateContext", { scope: scopeName, from: fmt.date(from), to: fmt.date(to) })}
              </p>
            </div>
            <div className="lay-expand-controls">
              <ExpandedRangePicker
                range={range} from={from} to={to} latestDay={latestDay} onRangeChange={onRangeChange}
              />
              <div className="lay-expand-pager">
                <Button
                  ref={prevPageRef}
                  aria-label={t("expandPrev")} onClick={() => pageFromButton(-1)}
                  disabled={view.fits || view.atStart}
                  sx={{ "&&": { minWidth: 44, minHeight: 44 } }}
                >
                  <ChevronLeft size={18} aria-hidden focusable={false} />
                </Button>
                <Button
                  ref={nextPageRef}
                  aria-label={t("expandNext")} onClick={() => pageFromButton(1)}
                  disabled={view.fits || view.atEnd}
                  sx={{ "&&": { minWidth: 44, minHeight: 44 } }}
                >
                  <ChevronRight size={18} aria-hidden focusable={false} />
                </Button>
              </div>
            </div>
          </div>

          {failed ? <Alert severity="error" className="error">{t("panelLoadError")}</Alert>
            : data === null ? <LinearProgress aria-label={t("trendPanelTitle")} sx={{ height: 5, borderRadius: 2 }} />
              : <>
                <p className="lay-expand-maplabel">
                  <span>{t("expandMapLabel", { count: days, days: fmt.count(days) })}</span>
                  <span>{rangeSpan}</span>
                </p>
                {/* role="img": the map is a PICTURE of where you are, never the
                    only way to get there — the pager, Page Up/Down and the
                    strip's own arrow keys all move the window without it. */}
                <div
                  className={view.fits ? "daymap is-static" : "daymap"} ref={mapRef}
                  role="img" aria-label={t("expandMapLabel", { count: days, days: fmt.count(days) })}
                  onPointerDown={onMapPointerDown}
                >
                  {slots.map((slot) => (
                    <i
                      key={slot.date}
                      className={slot.kind === "partial" ? "daymap-partial"
                        : slot.kind === "recorded" ? "" : "daymap-none"}
                      style={{ height: `${slot.kind === "partial" || slot.kind === "recorded" ? slot.heightPct : 0}%` }}
                    />
                  ))}
                  <span className="window-box" ref={boxRef} />
                </div>

                <figure className="trend lay-expand-figure">
                  <figcaption className="trend-scale">
                    <span>{title}</span>
                    <span className="trend-figures">
                      <span className="trend-avg">{average}</span>
                      <span className="trend-peak">{peak}</span>
                    </span>
                  </figcaption>
                  <DayReadout strip={strip} tip={tip} />
                  <div className="lay-expand-plot">
                    {/* The gutter sits OUTSIDE the scroller rather than being
                        sticky inside it: same result, and a momentum fling
                        cannot briefly drag it away. */}
                    <div className="lay-expand-axis" ref={gutterRef} aria-hidden="true">
                      <span className="lay-expand-axis-top">{data.max === null ? "—" : fmt.count(data.max)}</span>
                      <span className="lay-expand-axis-zero">{fmt.count(0)}</span>
                    </div>
                    <div className="lay-expand-window">
                      <div
                        className="lay-expand-scroll" ref={scrollerRef}
                        onScroll={() => { sync(); strip.placeReadout(); }}
                      >
                        <DaySlots variant="expanded" data={data} label={label} tip={tip} strip={strip} />
                        <div className="datebar" aria-hidden="true">
                          {slots.map((slot, i) => (
                            <span key={slot.date}>
                              {(days - 1 - i) % TICK_DAYS === 0 && (
                                <b className={i === days - 1 ? "date-tick is-end"
                                  : i < EDGE_TICK_SLOTS ? "date-tick is-start" : "date-tick"}>
                                  {fmt.date(slot.date)}
                                </b>
                              )}
                            </span>
                          ))}
                        </div>
                      </div>
                      {/* Outside the scroller, so each cue stays at an edge of
                          the REGION. Inside it they are positioned against the
                          scrolled content instead, which drew a pale band down
                          the middle of the chart (#941, lab finding 5). */}
                      <span className="scroll-cue left" aria-hidden="true" hidden={view.fits || view.atStart} />
                      <span className="scroll-cue right" aria-hidden="true" hidden={view.fits || view.atEnd} />
                    </div>
                  </div>
                  <div className="lay-expand-foot">
                    <DayLegend legend={legend} />
                    {/* One caption for the eye, one for the ear. The visible
                        span never lags the chart; the live region waits for
                        the window to settle. Splitting them is what keeps the
                        same sentence from being read twice. */}
                    <span className="lay-expand-shown" aria-hidden="true">{shownNote}</span>
                    <span className="sr-only" aria-live="polite">{announced}</span>
                  </div>
                </figure>
              </>}
        </div>
        {/* The one way out takes focus on open. `data-mui-focusable` is how
            `FocusTrap` is told which descendant to open on, and it is MUI's own
            attribute (`utils/focusable.js`) — a rename on upgrade would fall
            back to the frame's root, which is what the opening-focus assertion
            in ExpandedLayRate.test.tsx is there to catch. */}
        <Button
          data-mui-focusable variant="contained" onClick={onClose}
          className="lay-expand-leave" sx={{ "&&": { minHeight: 44 } }}
        >
          {t("expandClose")}
        </Button>
        <span className="lay-expand-hint">{t("expandEscapeHint")}</span>
      </div>
      </div>
    </Modal>
  );
}

// The same native select and plain date fields the card uses (#914), held to
// the expanded chart's own 90-day ceiling.
function ExpandedRangePicker({ range, from, to, latestDay, onRangeChange }: {
  range: LayRateRange;
  // The window on screen, which is what the custom form opens on: switching to
  // Custom range offers the span being looked at, not a one-day stub.
  from: string;
  to: string;
  latestDay: string;
  onRangeChange: (next: LayRateRange) => void;
}) {
  const { t } = useTranslation("dashboard");
  const fmt = useFormat();
  const [custom, setCustom] = useState(range.kind === "custom");
  const [draftFrom, setDraftFrom] = useState(from);
  const [draftTo, setDraftTo] = useState(to);
  const [error, setError] = useState<CustomRangeError | null>(null);

  const select = (value: string) => {
    setError(null);
    const preset = EXPANDED_RANGE_PRESETS.find((days) => String(days) === value);
    if (preset === undefined) {
      setDraftFrom(from);
      setDraftTo(to);
      setCustom(true);
      return;
    }
    setCustom(false);
    onRangeChange({ kind: "preset", days: preset });
  };
  const apply = () => {
    const next = customRangeError(draftFrom, draftTo, latestDay, MAX_EXPANDED_RANGE_DAYS);
    setError(next);
    if (next === null) onRangeChange({ kind: "custom", from: draftFrom, to: draftTo });
  };
  const errorText = (kind: CustomRangeError) => {
    switch (kind) {
      case "order": return t("rangeErrorOrder");
      case "future": return t("rangeErrorFuture", { date: fmt.date(latestDay) });
      case "tooLong": return t("rangeErrorTooLong", { count: MAX_EXPANDED_RANGE_DAYS, days: fmt.count(MAX_EXPANDED_RANGE_DAYS) });
      case "incomplete": return t("rangeErrorIncomplete");
      case "beforeCalendar": return t("rangeErrorBeforeCalendar");
    }
  };

  return (
    <div className="lay-expand-range">
      <TextField
        select size="small" label={t("rangeLabel")}
        value={range.kind === "custom" || custom ? "custom" : String(range.days)}
        slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
        onChange={(e) => select(e.target.value)}
        sx={{ minWidth: "11rem" }}
      >
        {EXPANDED_RANGE_PRESETS.map((days) => (
          <option key={days} value={String(days)}>
            {t("rangePresetOption", { count: days, total: fmt.count(days) })}
          </option>
        ))}
        <option value="custom">{t("rangeCustomOption")}</option>
      </TextField>
      {custom && (
        <div className="lay-expand-custom">
          {/* Sized in styles.css, not here: the phone rule is a change of flex
              DIRECTION, and a flex-basis written as an sx prop would then be a
              height. */}
          <FilterDateField
            className="lay-expand-date"
            label={t("rangeFromLabel")} value={draftFrom}
            slotProps={{ htmlInput: { max: latestDay } }}
            onChange={(e) => setDraftFrom(e.target.value)}
            sx={{ maxWidth: "none" }}
          />
          <FilterDateField
            className="lay-expand-date"
            label={t("rangeToLabel")} value={draftTo}
            slotProps={{ htmlInput: { max: latestDay } }}
            onChange={(e) => setDraftTo(e.target.value)}
            sx={{ maxWidth: "none" }}
          />
          <Button variant="outlined" color="inherit" onClick={apply} sx={{ "&&": { minHeight: 44 } }}>
            {t("rangeApply")}
          </Button>
        </div>
      )}
      {error !== null && <Alert severity="error" className="error">{errorText(error)}</Alert>}
    </div>
  );
}
