// web/src/components/DayStrip.tsx
import type { DayStripData } from "../lib/dashboard";

// #654 → #777 — the Dashboard's "Last 14 days", one slot per day. Geometry
// arrives computed (lib/dashboard.ts dayStrip) so this stays a pure renderer,
// and `label` names the picture for a screen reader (role="img").
//
// A STRIP of days, not a line. Stated at the strength the code supports: the
// line mapped a zero day to the floor of its viewBox, so a zero WAS drawn as a
// drop. What it never drew was that floor or the top of the scale, so nothing
// said the bottom meant zero rather than the window's own minimum; and it
// interpolated between days a daily count has no values between. A slot is
// drawn for every day in the window, so a day with no figure is a visible gap —
// which does NOT make it distinguishable from a true zero day (#780).
//
// `title`, `peak` and the two rule dates arrive already formatted in the farm's
// locale (#650).
export function DayStrip({ data, label, title, peak, from, to }: {
  data: DayStripData;
  label: string;
  title: string;
  peak: string;
  from: React.ReactNode;
  to: React.ReactNode;
}) {
  return (
    <figure className="trend">
      <figcaption className="trend-scale">
        <span>{title}</span>
        <span className="trend-peak">{peak}</span>
      </figcaption>
      <div className="daystrip" role="img" aria-label={label}>
        {data.slots.map((s) => (
          <span key={s.date} className={s.weekBreak ? "day day-week" : "day"}>
            {s.recorded && <i style={{ height: `${s.heightPct}%` }} />}
          </span>
        ))}
      </div>
      <div className="trend-rule">
        <span>{from}</span>
        <span>{to}</span>
      </div>
    </figure>
  );
}
