import { useState } from "react";
import type { FormEvent } from "react";
import { Box, Stack, TextField, ToggleButton, ToggleButtonGroup, Typography } from "@mui/material";
import { useTranslation } from "react-i18next";
import type { Flock, WaterUsage } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { waterSourceLabel, waterUnitLabel } from "../i18n/enums";
import { useFormat } from "../farm/useFormat";
import { BusyButton } from "../components/BusyButton";
import { ConsoleSummary, CONSOLE_TICKET_SX, CONSOLE_TICKET_FORM_SX, CONSOLE_TICKET_CHECK_SX } from "../components/FieldConsole";
import { FlockPicker } from "../components/FlockPicker";
import type { PickerSnapshot } from "../components/NamedEntityPicker";
import i18n from "../i18n";

const SOURCES = ["Well", "Municipal", "Tank", "Other"];
const UNITS = ["L", "gal"];

export function errText(err: unknown): string {
  // Concurrent-edit conflicts get a human message instead of raw problem text.
  if (err instanceof ApiError && err.status === 409)
    return i18n.t("water:concurrentEditError");
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

export function useWaterCaptureFields(today: string) {
  // capture form; editingId switches it to update mode. editingVersion is the
  // base Version the row was loaded with — sent back so a concurrent edit
  // surfaces as a 409 instead of being silently overwritten.
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editingVersion, setEditingVersion] = useState(0);
  // #512 (T027/T037) — the capture flock is committed through FlockPicker.
  // `captureFlock` is the page-controlled committed entity; bumping
  // `captureFlockGen` re-syncs the engine's committed state after an external
  // reset (the mount-time default, a post-edit reset, or a startEdit
  // row-owned value — possibly an archived flock absent from discovery).
  // `captureFlockSnapshot.canSubmit` gates BOTH the submit button and
  // onSubmit itself.
  const [captureFlock, setCaptureFlock] = useState<Flock | null>(null);
  const [captureFlockGen, setCaptureFlockGen] = useState(0);
  // #512 (T037) — a row-owned capture flock id whose FULL entity is not in the
  // loaded list (typically an Archived flock the active/depleted discovery
  // never carries). While set, `captureFlock` stays null — nothing fabricated
  // can render or submit — and the id flows through the picker's `requestedId`
  // exact GET, which commits the true entity (status/breed/etc. included) or
  // enters `unavailable` (canSubmit false → no save).
  const [captureFlockRequestId, setCaptureFlockRequestId] = useState<string | null>(null);
  const [captureFlockSnapshot, setCaptureFlockSnapshot] = useState<PickerSnapshot<Flock>>({
    committed: null, selectionPhase: "uninitialized", exploring: false, canSubmit: false,
  });
  const [capturePickerOpen, setCapturePickerOpen] = useState(false);
  const [date, setDate] = useState(today);
  const [source, setSource] = useState("Well");
  const [unit, setUnit] = useState("L");
  const [useMeters, setUseMeters] = useState(false);
  const [quantity, setQuantity] = useState("");
  const [meterStart, setMeterStart] = useState("");
  const [meterEnd, setMeterEnd] = useState("");
  const [note, setNote] = useState("");
  return {
    editingId, setEditingId,
    editingVersion, setEditingVersion,
    captureFlock, setCaptureFlock,
    captureFlockGen, setCaptureFlockGen,
    captureFlockRequestId, setCaptureFlockRequestId,
    captureFlockSnapshot, setCaptureFlockSnapshot,
    capturePickerOpen, setCapturePickerOpen,
    date, setDate,
    source, setSource,
    unit, setUnit,
    useMeters, setUseMeters,
    quantity, setQuantity,
    meterStart, setMeterStart,
    meterEnd, setMeterEnd,
    note, setNote,
  };
}

export type WaterCaptureFields = ReturnType<typeof useWaterCaptureFields>;

export function WaterCaptureForm({
  isDesktop, fields, today, editingId, editingRow, busy, onSubmit, onReset, error, message,
}: {
  isDesktop: boolean;
  fields: WaterCaptureFields;
  today: string;
  editingId: string | null;
  editingRow: WaterUsage | null;
  busy: boolean;
  onSubmit: (e: FormEvent) => void;
  onReset: () => void;
  error: string | null;
  message: string | null;
}) {
  const { t } = useTranslation("water");
  const fmt = useFormat();
  const {
    captureFlock, setCaptureFlock,
    captureFlockGen, setCaptureFlockGen,
    captureFlockRequestId, setCaptureFlockRequestId,
    captureFlockSnapshot, setCaptureFlockSnapshot,
    capturePickerOpen, setCapturePickerOpen,
    date, setDate,
    source, setSource,
    unit, setUnit,
    useMeters, setUseMeters,
    quantity, setQuantity,
    meterStart, setMeterStart,
    meterEnd, setMeterEnd,
    note, setNote,
  } = fields;
  // Meter readings are stored to three decimal places.
  const meterQuantity = (Math.round(Number(meterEnd) * 1000) - Math.round(Number(meterStart) * 1000)) / 1000;
  return (
    <>
      {isDesktop && <ConsoleSummary label={t("contextLabel")} items={[
        { label: t("entryMode"), value: t(useMeters ? "meterMode" : "directMode") },
        { label: t("sourceLabel"), value: waterSourceLabel(source) },
        { label: t("unitLabel"), value: waterUnitLabel(unit) },
      ]} />}
      <Box sx={isDesktop ? CONSOLE_TICKET_SX : { ...CONSOLE_TICKET_SX, border: 0, my: 0 }}>
        <Box sx={{ p: isDesktop ? 2 : 0, minWidth: 0 }}>
          <ToggleButtonGroup exclusive value={useMeters ? "meter" : "direct"} aria-label={t("entryMode")} fullWidth sx={{ mb: 2, "&& .MuiToggleButton-root.Mui-selected, && .MuiToggleButton-root.Mui-selected:hover": { bgcolor: "var(--brand)", color: "var(--on-brand)" } }}>
            <ToggleButton value="direct" onClick={() => setUseMeters(false)}>{t("directMode")}</ToggleButton>
            <ToggleButton value="meter" onClick={() => setUseMeters(true)}>{t("meterMode")}</ToggleButton>
          </ToggleButtonGroup>
        <Stack component="form" id="water-entry-form" sx={CONSOLE_TICKET_FORM_SX} onSubmit={onSubmit}>
          <Box sx={{ minWidth: 0, width: "100%" }}>
            <FlockPicker
              label={t("flockLabel")}
              eligibility="active-and-depleted"
              required
              disabled={editingId !== null}
              open={capturePickerOpen}
              controlledCommitted={captureFlock}
              controlledGeneration={captureFlockGen}
              requestedId={captureFlockRequestId}
              onSnapshot={(snap) => {
                setCaptureFlockSnapshot(snap);
                // Controlled re-emissions may carry the previous default until the exact lookup resolves.
                if (
                  snap.committed &&
                  captureFlockRequestId &&
                  snap.committed.id === captureFlockRequestId
                ) {
                  setCaptureFlock(snap.committed);
                  setCaptureFlockRequestId(null);
                }
              }}
              onCommit={(f) => {
                setCaptureFlock(f);
                setCaptureFlockRequestId(null);
                setCaptureFlockGen((g) => g + 1);
                setCapturePickerOpen(false);
              }}
              onEscape={() => setCapturePickerOpen(false)}
              onOutsideClick={() => setCapturePickerOpen(false)}
              trigger={
                <button
                  type="button"
                  className="named-picker-trigger"
                  disabled={editingId !== null}
                  onClick={() => setCapturePickerOpen(true)}
                >
                  {captureFlock
                    ? `${captureFlock.name}${captureFlock.status === "Depleted" ? t("depletedFlockSuffix") : ""}`
                    // Display the saved flock name until its exact lookup resolves.
                    : (editingRow && captureFlockRequestId ? editingRow.flockName : null)
                      ?? t("selectFlockOption")}
                </button>
              }
            />
          </Box>
          <TextField
            type="date"
            label={t("dateLabel")}
            value={date}
            size="small"
            disabled={editingId !== null}
            slotProps={{ htmlInput: { max: today, required: true }, inputLabel: { shrink: true } }}
            onChange={(e) => setDate(e.target.value)}
          />
          <TextField
            select
            label={t("sourceLabel")}
            value={source}
            size="small"
            slotProps={{ select: { native: true } }}
            onChange={(e) => setSource(e.target.value)}
          >
            {SOURCES.map((s) => <option key={s} value={s}>{waterSourceLabel(s)}</option>)}
          </TextField>
          <TextField
            select
            label={t("unitLabel")}
            value={unit}
            size="small"
            slotProps={{ select: { native: true } }}
            onChange={(e) => setUnit(e.target.value)}
          >
            {UNITS.map((u) => <option key={u} value={u}>{waterUnitLabel(u)}</option>)}
          </TextField>
          {useMeters ? (
            <>
              <TextField
                type="number"
                label={t("meterStartLabel")}
                value={meterStart}
                size="small"
                slotProps={{ htmlInput: { min: 0, step: 0.001, required: true } }}
                onChange={(e) => setMeterStart(e.target.value)}
              />
              <TextField
                type="number"
                label={t("meterEndLabel")}
                value={meterEnd}
                size="small"
                slotProps={{ htmlInput: { min: 0, step: 0.001, required: true } }}
                onChange={(e) => setMeterEnd(e.target.value)}
              />
            </>
          ) : (
            <TextField
              type="number"
              label={t("quantityLabelWithUnit", { unit: waterUnitLabel(unit) })}
              value={quantity}
              size="small"
              slotProps={{ htmlInput: { min: 0.001, step: 0.001, required: true } }}
              onChange={(e) => setQuantity(e.target.value)}
            />
          )}
          <TextField
            label={t("noteLabel")}
            sx={{ gridColumn: { md: useMeters ? "auto" : "span 2" } }}
            value={note}
            size="small"
            slotProps={{ htmlInput: { maxLength: 500 } }}
            onChange={(e) => setNote(e.target.value)}
          />
          {isDesktop && <BusyButton variant="contained" type="submit" busy={busy}
            disabled={!captureFlock || !captureFlockSnapshot.canSubmit}>
            {editingId ? t("saveCorrectionButton") : t("recordWaterButton")}
          </BusyButton>}
          {isDesktop && editingId && (
            <button type="button" className="link" onClick={onReset}>{t("cancelEditButton")}</button>
          )}
        </Stack>
        </Box>
        <Box component="aside" aria-label={t("readingCheck")} sx={isDesktop ? CONSOLE_TICKET_CHECK_SX : { ...CONSOLE_TICKET_CHECK_SX, p: 1.5, mt: 2, border: "1px solid var(--rule)", borderRadius: "5px" }}>
          <h3>{t("readingCheck")}</h3>
          <Box component="dl" sx={{ m: 0 }}>
            <Box sx={{ display: "flex", justifyContent: "space-between", gap: 2, py: 1.25, borderBottom: "1px solid var(--rule)" }}>
              <Typography component="dt">{t("sourceLabel")}</Typography>
              <Typography component="dd" sx={{ m: 0, fontWeight: 700 }}>{waterSourceLabel(source)}</Typography>
            </Box>
            <Box aria-live="polite" aria-atomic="true" sx={{ display: "flex", justifyContent: "space-between", gap: 2, py: 1.25, borderBottom: "1px solid var(--rule)" }}>
              <Typography component="dt">{t("result")}</Typography>
              <Typography component="dd" sx={{ m: 0, fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>
                {useMeters
                  ? meterStart !== "" && meterEnd !== "" ? `${fmt.count(meterQuantity)} ${waterUnitLabel(unit)}` : "—"
                  : quantity !== "" ? `${fmt.count(Number(quantity))} ${waterUnitLabel(unit)}` : "—"}
              </Typography>
            </Box>
          </Box>
        </Box>
      </Box>

      {error && <p className="error">{error}</p>}
      {isDesktop && message && <p className="success">{message}</p>}

    </>
  );
}
