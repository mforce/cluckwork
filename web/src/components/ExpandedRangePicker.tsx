// web/src/components/ExpandedRangePicker.tsx
//
// #914/#941 — the expanded Lay rate chart's range control. Split out of
// ExpandedLayRate.tsx, which holds the chart itself.
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Alert, Button, TextField } from "@mui/material";
import { FilterDateField } from "./FilterBar";
import { useFormat } from "../farm/useFormat";
import {
  EXPANDED_RANGE_PRESETS, MAX_EXPANDED_RANGE_DAYS, customRangeError,
} from "../lib/layRateRange";
import type { CustomRangeError, LayRateRange } from "../lib/layRateRange";

// The same native select and plain date fields the card uses (#914), held to
// the expanded chart's own one-year ceiling.
export function ExpandedRangePicker({ range, from, to, latestDay, onRangeChange }: {
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
          {/* Sized in styles/expanded-lay-rate.css, not here: the phone rule is a change of flex
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
