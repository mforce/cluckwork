import { useId } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Alert, Box, Button, Card, LinearProgress, TextField, Typography } from "@mui/material";
import { ChevronRight, Maximize2 } from "lucide-react";
import type { Dispatch, ReactNode, RefObject, SetStateAction } from "react";
import type { Flock, ProductionReport } from "../../api/cluckwork";
import { useFormat } from "../../farm/useFormat";
import { FarmDate } from "../../components/FarmDate";
import { DayStrip } from "../../components/DayStrip";
import { HenDayOver100Flag } from "../../components/HenDayOver100Flag";
import { Dialog } from "../../components/Dialog";
import { FilterDateField } from "../../components/FilterBar";
import { FlockPicker } from "../../components/FlockPicker";
import {
  dayStrip, henDayTrend,
} from "../../lib/dashboard";
import {
  MAX_RANGE_DAYS, RANGE_PRESETS, customRangeError, trendWindow,
} from "../../lib/layRateRange";
import type { CustomRangeError, LayRateRange } from "../../lib/layRateRange";
import type { FlockScope } from "../Dashboard";
import { headingSx, sectionSx } from "./panelSx";
import { useTrendStrings } from "./trendStrings";

// The native select's own control box carries the 44px phone target; MUI sizes
// it from the font otherwise.
const RANGE_FIELD_SX = { "& .MuiInputBase-input": { minHeight: 44, boxSizing: "border-box" } };

// The Lay rate card of the Dashboard (#1215): the fortnight strip with its
// scope selector, range control and hen-day KPI. It renders at exactly the
// page's old position in the panel grid; the range and scope state, the
// trend reads and the account-scoped storage keys stay on the page and are
// passed in.
export function LayRateCard({
  scopedFlock,
  soleFlock,
  scope, setScope,
  pickerOpen, setPickerOpen,
  accessibleCountLabel,
  flocksFailed, flocksRetrying, retryFlocks,
  range, chooseRange,
  customOpen, setCustomOpen,
  draftFrom, setDraftFrom,
  draftTo, setDraftTo,
  rangeError, setRangeError,
  latestDay,
  contextScope,
  plotted, trend, trendLoading,
  panelError,
  expandRef, setExpanded,
  t, tp, fmt,
}: {
  scopedFlock: Flock | null;
  soleFlock: Flock | null;
  scope: FlockScope; setScope: Dispatch<SetStateAction<FlockScope>>;
  pickerOpen: boolean; setPickerOpen: Dispatch<SetStateAction<boolean>>;
  accessibleCountLabel: string;
  flocksFailed: boolean;
  flocksRetrying: boolean;
  retryFlocks: () => void;
  range: LayRateRange; chooseRange: (next: LayRateRange) => void;
  customOpen: boolean; setCustomOpen: Dispatch<SetStateAction<boolean>>;
  draftFrom: string; setDraftFrom: Dispatch<SetStateAction<string>>;
  draftTo: string; setDraftTo: Dispatch<SetStateAction<string>>;
  rangeError: CustomRangeError | null;
  setRangeError: Dispatch<SetStateAction<CustomRangeError | null>>;
  latestDay: string;
  contextScope: string;
  plotted: ReturnType<typeof trendWindow>;
  trend: { current: ProductionReport; previous: ProductionReport } | null;
  trendLoading: boolean;
  panelError: ReactNode;
  expandRef: RefObject<HTMLButtonElement | null>;
  setExpanded: Dispatch<SetStateAction<boolean>>;
  t: ReturnType<typeof useTranslation<"dashboard">>["t"];
  tp: ReturnType<typeof useTranslation<"namedEntityPicker">>["t"];
  fmt: ReturnType<typeof useFormat>;
}) {
  const { deltaText, deltaClass, stripStrings } = useTrendStrings();
  const scopeLabelId = useId();
  const scopeValueId = useId();
  const { from, to } = plotted;
  const scopeName = scopedFlock ? scopedFlock.name : t("allFlocksOption");
  const trendData = trend === null ? null : {
    line: dayStrip({ days: trend.current.days }),
    henDay: henDayTrend(trend.current, trend.previous),
    henDayExposure: { ratedEggs: trend.current.totalRatedEggs, recordedHenDays: trend.current.totalRecordedHenDays },
  };
  // The window's own dates, which every sentence about the card's range uses
  // rather than a preset's wording: a custom range has no preset to name, and
  // one string cannot be built from a stem plus a suffix (#650).
  const rangeSpan = t("rangeSpan", { from: fmt.date(from), to: fmt.date(to) });
  const presetLabel = (days: number) => t("rangePresetOption", { count: days, total: fmt.count(days) });
  const rangeSelection = range.kind === "custom" || customOpen ? "custom" : String(range.days);
  const selectRange = (value: string) => {
    setRangeError(null);
    const preset = RANGE_PRESETS.find((days) => String(days) === value);
    if (preset === undefined) {
      setDraftFrom(from);
      setDraftTo(to);
      setCustomOpen(true);
      return;
    }
    setCustomOpen(false);
    chooseRange({ kind: "preset", days: preset });
  };
  // Rejected in the form, never silently truncated: a window the card quietly
  // shortened would leave the reader comparing a period they did not ask for.
  const applyCustomRange = () => {
    const error = customRangeError(draftFrom, draftTo, latestDay);
    setRangeError(error);
    if (error === null) chooseRange({ kind: "custom", from: draftFrom, to: draftTo });
  };
  const rangeErrorText = (error: CustomRangeError) => {
    switch (error) {
      case "order": return t("rangeErrorOrder");
      case "future": return t("rangeErrorFuture", { date: fmt.date(latestDay) });
      case "tooLong": return t("rangeErrorTooLong", { count: MAX_RANGE_DAYS, days: fmt.count(MAX_RANGE_DAYS) });
      case "incomplete": return t("rangeErrorIncomplete");
      case "beforeCalendar": return t("rangeErrorBeforeCalendar");
    }
  };

  return (
    <Card component="section" sx={{ ...sectionSx, gridColumn: { md: 2 }, gridRow: { md: 2 },
      mx: { xs: "calc(7px - 1.15rem)", md: 0 },
      "& .trend-fig": { fontFamily: "Georgia, serif", fontSize: "2.5rem" },
      "& .daystrip": { display: "flex", gap: { xs: "2px", md: "4px" }, height: 80 },
      "& .day": { height: 80 },
      "& .day > i": { maxWidth: { xs: "none", md: 18 } },
      "& .tipdock .tip": { whiteSpace: "normal", overflow: "visible", maxWidth: "100%" },
    }}>
      {/* #916/#918 — matches the approved mockup's DOM order exactly
          (production-flock-selector-v2.html): head, scope, context,
          scale+dock+strip+rule+legend (all inside DayStrip), hen-day KPI
          LAST. #914 adds the range control after the scope and drops the
          head's fixed "Last 14 days" caption, which the control replaces. */}
      <Box sx={headingSx}>
        <Typography variant="h3" aria-label={t("trendPanelTitle")}><Link to="/reports">{t("layRateTitle")}</Link></Typography>
        {/* #941 — the card draws at most fourteen real days (#940). Longer
            ranges live behind this control, on a surface wide enough to
            keep every bar 22px. */}
        <Button
          ref={expandRef} size="small" startIcon={<Maximize2 size={16} aria-hidden focusable={false} />}
          aria-label={t("expandAriaLabel")} onClick={() => setExpanded(true)}
          sx={{ textTransform: "none", "&&": { minHeight: 44 } }}
        >
          {t("expandLabel")}
        </Button>
      </Box>

      {flocksFailed ? (
        // The panel-error pattern rather than an empty selector: a failed
        // flock-list read must not read as a genuinely empty farm.
        <Box sx={{ mt: "18px", mb: "8px" }}>
          <Alert severity="error" className="error" action={
            <Button color="inherit" size="small" disabled={flocksRetrying} onClick={retryFlocks}>
              {flocksRetrying ? tp("loading") : tp("retry")}
            </Button>
          }>
            {t("flockListUnavailableMessage")}
          </Alert>
        </Box>
      ) : (
        <>
          <Box sx={{ mt: "18px", mb: "8px" }}>
            <Typography
              id={scopeLabelId} component="span"
              sx={{ display: "block", textTransform: "uppercase", letterSpacing: "0.08em", fontSize: "10px", color: "text.secondary", mb: "5px" }}
            >
              {t("flockScopeLabel")}
            </Typography>
            {soleFlock !== null ? (
              // The mockup's #fixedScope: plain text, no dropdown, no
              // chevron, no redundant All flocks choice (SELECTION.md).
              <Box sx={{ fontSize: "16px", fontWeight: 600, minHeight: "44px", display: "flex", alignItems: "center", borderBottom: "1px solid var(--rule)" }}>
                {soleFlock.name}
              </Box>
            ) : (
              <Button
                aria-labelledby={`${scopeLabelId} ${scopeValueId}`}
                aria-haspopup="dialog"
                aria-expanded={pickerOpen}
                onClick={() => setPickerOpen(true)}
                sx={{
                  width: "100%", display: "flex", justifyContent: "space-between", alignItems: "center",
                  fontWeight: 600, textAlign: "left", textTransform: "none", gap: 1.5, "&&": { minHeight: 44 },
                }}
              >
                <Box component="span" id={scopeValueId} sx={{ overflowWrap: "anywhere" }}>{scopeName}</Box>
                <ChevronRight size={18} aria-hidden focusable={false} />
              </Button>
            )}
          </Box>
          {/* #914 — a native select and two plain date fields, the shape
              every other filter on the app already uses. The MUI date
              range picker is a paid @mui/x package and stays out of the
              822 plan's dependency set. */}
          <Box sx={{ mb: "8px" }}>
            <TextField
              select
              fullWidth
              size="small"
              label={t("rangeLabel")}
              value={rangeSelection}
              slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
              onChange={(e) => selectRange(e.target.value)}
              sx={RANGE_FIELD_SX}
            >
              {RANGE_PRESETS.map((days) => <option key={days} value={String(days)}>{presetLabel(days)}</option>)}
              <option value="custom">{t("rangeCustomOption")}</option>
            </TextField>
            {customOpen && (
              <Box sx={{ display: "flex", flexWrap: "wrap", gap: 1, mt: 1, alignItems: "flex-end" }}>
                <FilterDateField
                  label={t("rangeFromLabel")} value={draftFrom}
                  slotProps={{ htmlInput: { max: latestDay } }}
                  onChange={(e) => setDraftFrom(e.target.value)}
                  sx={{ flex: "1 1 8rem", maxWidth: "none" }}
                />
                <FilterDateField
                  label={t("rangeToLabel")} value={draftTo}
                  slotProps={{ htmlInput: { max: latestDay } }}
                  onChange={(e) => setDraftTo(e.target.value)}
                  sx={{ flex: "1 1 8rem", maxWidth: "none" }}
                />
                <Button variant="outlined" color="inherit" onClick={applyCustomRange} sx={{ "&&": { minHeight: 44 } }}>
                  {t("rangeApply")}
                </Button>
              </Box>
            )}
            {rangeError !== null && (
              <Alert severity="error" className="error" sx={{ mt: 1 }}>{rangeErrorText(rangeError)}</Alert>
            )}
          </Box>
          <Typography className="trend-context" variant="caption" color="text.secondary" sx={{ display: "block" }}>
            {t("layRateContext", { scope: contextScope, from: fmt.date(from), to: fmt.date(to) })}
          </Typography>

          {soleFlock === null && (
            // #916 review — reuses the shared NamedEntityPicker/FlockPicker
            // (docs/designs/822-mui-revamp.md's NamedEntityPicker↔Autocomplete
            // row) instead of a bespoke dialog. "All flocks" pins above the
            // scrolling results via `pinnedChoice`, the same `slots.paper`
            // mechanism the picker already uses for its Load-more footer.
            // `onEscape`/`onOutsideClick` are no-ops, matching ExpensesPage's
            // own FlockPicker-in-Dialog usage: the keydown still bubbles to
            // the Dialog's own Escape/backdrop close.
            <Dialog open={pickerOpen} title={t("chooseFlockTitle")} onClose={() => setPickerOpen(false)}>
              {/* #918 P2 review — the shared Dialog unmounts its children
                  on close (MUI's default `keepMounted={false}`), so every
                  reopen is a FRESH engine mount. `controlledCommitted`
                  seeds `state.selection.entity` at that mount from the
                  current scope, driving `aria-selected` on the matching
                  option. `controlledGeneration` is a constant: the sync
                  effect fires once per mount regardless (its own ref
                  starts at -1), and `scope` never changes without the
                  dialog also closing, so no mount ever needs a second
                  sync. This also pre-fills the reopened search field
                  with the committed name — checked against the DOM, not
                  assumed (`Dashboard.test.tsx`) — matching every other
                  `FlockPicker`/`CustomerPicker` caller's own reopen
                  behaviour; the results are not narrowed by it, since the
                  seed touches only display text, never the discovery
                  filter. `onClear` is wired because seeding a non-null
                  `controlledCommitted` also surfaces the footer's own
                  Clear link (previously dead code) — left unwired it
                  would blank the engine's selection without touching
                  `scope`, reopening a version of this same desync. */}
              {/* #935 — Dialog's Paper has no width on desktop, only a
                  maxWidth, so it shrink-wraps to content; this is the
                  only FlockPicker caller outside a `.form-grid`, so
                  `.dialog .form-grid .named-picker { width: 100% }`
                  never reaches it. A percentage width wouldn't widen a
                  shrink-to-fit ancestor either — `min-width` does. 27rem
                  is the 30rem cap minus DialogContent's 24px each-side
                  padding. Scoped to `md`: below it Dialog.tsx already
                  sets an explicit phone width.

                  #937 — nothing follows the picker, so Paper's real
                  height was just the 44px search row and MUI clipped
                  the results popover (`position: absolute`, out of
                  flow). Reserve height instead of disabling the clip:
                  `overflow: visible` also changes what Popper's `flip`
                  modifier reads as available space, flipping the
                  popover above the search field. `dashboard-flock-
                  picker` (styles/named-picker.css) caps the listbox at ~6 rows
                  instead of MUI's 40vh default, so the modal stays
                  compact and only the listbox itself scrolls; 22.5rem
                  matches that cap plus the pinned choice button, the
                  meta/footer row and the search row itself. */}
              <Box className="dashboard-flock-picker" sx={{ minWidth: { md: "27rem" }, minHeight: "22.5rem" }}>
                <FlockPicker
                  label={t("searchAccessibleFlocksLabel")}
                  eligibility="active-and-depleted"
                  required={false}
                  open={pickerOpen}
                  onEscape={() => {}}
                  onOutsideClick={() => {}}
                  controlledCommitted={scope.kind === "flock" ? scope.flock : null}
                  controlledGeneration={1}
                  onCommit={(f) => { setScope({ kind: "flock", flock: f }); setPickerOpen(false); }}
                  onClear={() => { setScope({ kind: "all" }); setPickerOpen(false); }}
                  pinnedChoice={
                    <Button
                      fullWidth
                      onClick={() => { setScope({ kind: "all" }); setPickerOpen(false); }}
                      aria-pressed={scope.kind === "all"}
                      sx={{ justifyContent: "space-between", textTransform: "none", mx: 2, mt: 1, width: "calc(100% - 32px)", "&&": { minHeight: 44 } }}
                    >
                      <span>{t("allFlocksOption")}</span>
                      <Typography component="span" variant="caption" color="text.secondary">{accessibleCountLabel}</Typography>
                    </Button>
                  }
                />
              </Box>
            </Dialog>
          )}
        </>
      )}

      {trendLoading ? <LinearProgress aria-label={t("trendPanelTitle")} sx={{ height: 5, borderRadius: 2, mb: 1 }} />
        : trendData === null ? panelError : <>
          <DayStrip {...stripStrings(trendData.line, rangeSpan)}
            data={trendData.line} from={<FarmDate iso={from} />} to={<FarmDate iso={to} />} />
          <Typography className="trend-kpi"><span className="trend-fig">{trendData.henDay.current === null ? "—" : `${fmt.count(trendData.henDay.current, 1)}%`}</span><HenDayOver100Flag {...trendData.henDayExposure} /><span className={deltaClass(trendData.henDay.delta)}>{deltaText(trendData.henDay.delta)}</span></Typography>
          <Typography className="trend-sub" variant="caption" sx={{ display: "block" }}>
            {t("henDaySubLabel", { range: rangeSpan, count: plotted.days, days: fmt.count(plotted.days) })}
          </Typography>
      </>}
    </Card>
  );
}
