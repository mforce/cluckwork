// web/src/components/DayStrip.tsx
import type { DayStripData, DayStripSlot } from "../lib/dashboard";
import { DaySlots } from "./DaySlots";
import { useDayStrip } from "./useDayStrip";

// #654 → #777 → #780 — the Dashboard's Lay rate strip, one slot per day.
// Geometry arrives computed (lib/dashboard.ts dayStrip) so this stays a pure
// renderer, and every string arrives already formatted in the farm's locale
// (#650): `tip` turns a slot into the sentence shown for it.
//
// A STRIP of days, not a line. The line it replaces drew a zero day on the
// floor of its viewBox, never drew that floor or the top of the scale, and
// interpolated between days a daily count has no values between. Since #780 a
// recorded day that produced nothing keeps a stub at the baseline, a day nobody
// recorded draws nothing, and a day only some houses reported is marked as a
// floor rather than a figure.
//
// One tab stop, not fourteen. The slots are peers, so they behave like a
// toolbar: Tab enters the strip at the selected day (the first, initially) and
// the arrow keys move along it. Fourteen consecutive tab stops on the way past
// a dashboard panel is not keyboard support, it is a keyboard obstacle. That
// behaviour lives in `useDayStrip`, shared with the expanded chart (#941).
export function DayStrip({ data, label, title, peak, average, legend, tip, from, to }: {
  data: DayStripData;
  label: string;
  title: string;
  peak: string;
  // Always a sentence, never hidden: the mockup states the average's ABSENCE
  // beside Peak rather than leaving a blank space there.
  average: string;
  // Translated by the caller — DayStrip stays props-only, never importing
  // i18n itself.
  legend: { complete: string; partial: string; noEntry: string };
  tip: (slot: DayStripSlot) => string;
  from: React.ReactNode;
  to: React.ReactNode;
}) {
  const strip = useDayStrip(data.slots, tip);

  return (
    <figure className="trend">
      <figcaption className="trend-scale">
        <span>{title}</span>
        <span className="trend-figures">
          <span className="trend-avg">{average}</span>
          <span className="trend-peak">{peak}</span>
        </span>
      </figcaption>
      <DayReadout strip={strip} tip={tip} />
      <DaySlots variant="card" data={data} label={label} tip={tip} strip={strip} />
      <div className="trend-rule">
        <span>{from}</span>
        <span>{to}</span>
      </div>
      <DayLegend legend={legend} />
    </figure>
  );
}

// A reserved row, always present, so the box appearing reflows nothing. It
// costs 2.2rem of panel height permanently — the honest price of a readout that
// covers neither the bars it describes nor the Avg and Peak figures the reader
// compares them against. Placed inside the plot it covered six neighbouring
// bars at the peak.
//
// aria-hidden, because the selected slot's own accessible name is this exact
// sentence. A live region here announced it a second time, and a live region
// mounted together with its text is unreliably announced anyway
// (NamedEntityPicker keeps a permanently mounted one for that reason).
export function DayReadout({ strip, tip }: {
  strip: ReturnType<typeof useDayStrip>;
  tip: (slot: DayStripSlot) => string;
}) {
  return (
    <div className="tipdock" ref={strip.dockRef} aria-hidden="true">
      {strip.active !== null && <span className="tip" ref={strip.tipRef}>{tip(strip.active)}</span>}
    </div>
  );
}

// What solid, hatched and outlined mean. Shared with the expanded chart, which
// shows the same three marks.
export function DayLegend({ legend }: { legend: { complete: string; partial: string; noEntry: string } }) {
  return (
    <ul className="trend-legend">
      <li><span className="trend-legend-swatch" aria-hidden="true" />{legend.complete}</li>
      <li><span className="trend-legend-swatch partial" aria-hidden="true" />{legend.partial}</li>
      <li><span className="trend-legend-swatch missing" aria-hidden="true" />{legend.noEntry}</li>
    </ul>
  );
}
