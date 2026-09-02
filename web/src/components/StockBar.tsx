// web/src/components/StockBar.tsx
import type { StockBarData } from "../lib/dashboard";

// #654 — the stock stacked bar: one span per grade on the `.meter-stack`
// track, widths and opacities precomputed (lib/dashboard.ts stockBar).
// Decorative: the screen's caption is the text of record, so the track is
// hidden from the accessibility tree like StockPage's `.meter`.
export function StockBar({ data }: { data: StockBarData }) {
  return (
    <div className="meter-stack" aria-hidden="true">
      {data.segments.map((s) => (
        <span key={s.eggGradeId} style={{ width: `${s.pct}%`, opacity: s.opacity }} />
      ))}
    </div>
  );
}
