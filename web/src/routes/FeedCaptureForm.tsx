import { useState } from "react";
import type { FormEvent } from "react";
import { Box, Stack, TextField, Typography } from "@mui/material";
import { useTranslation } from "react-i18next";
import type { Flock, InventoryItem } from "../api/cluckwork";
import { BusyButton } from "../components/BusyButton";
import { ConsoleSummary, CONSOLE_TICKET_SX, CONSOLE_TICKET_FORM_SX, CONSOLE_TICKET_CHECK_SX } from "../components/FieldConsole";
import { FlockPicker } from "../components/FlockPicker";
import type { PickerSnapshot } from "../components/NamedEntityPicker";
import { useFormat } from "../farm/useFormat";

export function useFeedCaptureFields(today: string) {
  // #512 (T027/T038) — the capture flock is committed through FlockPicker.
  // `captureFlock` is the page-controlled committed entity; bumping
  // `captureFlockGen` re-syncs the engine's committed state after an external
  // reset (the mount-time default) so a later Escape cannot resurrect a stale
  // ID. `flockSnapshot.canSubmit` gates BOTH the Record button and onSubmit.
  const [captureFlock, setCaptureFlock] = useState<Flock | null>(null);
  const [captureFlockGen, setCaptureFlockGen] = useState(0);
  const [captureFlockSnapshot, setCaptureFlockSnapshot] = useState<PickerSnapshot<Flock>>({
    committed: null, selectionPhase: "uninitialized", exploring: false, canSubmit: false,
  });
  const [capturePickerOpen, setCapturePickerOpen] = useState(false);
  const [itemId, setItemId] = useState("");
  const [date, setDate] = useState(today);
  const [quantity, setQuantity] = useState("");
  const [note, setNote] = useState("");
  return {
    captureFlock, setCaptureFlock,
    captureFlockGen, setCaptureFlockGen,
    captureFlockSnapshot, setCaptureFlockSnapshot,
    capturePickerOpen, setCapturePickerOpen,
    itemId, setItemId,
    date, setDate,
    quantity, setQuantity,
    note, setNote,
  };
}

export type FeedCaptureFields = ReturnType<typeof useFeedCaptureFields>;

export function FeedCaptureForm({
  isDesktop, fields, today, pickableItems, selectedItem, afterIssueQuantity, busy, onSubmit, error, message,
}: {
  isDesktop: boolean;
  fields: FeedCaptureFields;
  today: string;
  pickableItems: InventoryItem[];
  selectedItem: InventoryItem | undefined;
  afterIssueQuantity: number | null;
  busy: boolean;
  onSubmit: (e: FormEvent) => void;
  error: string | null;
  message: string | null;
}) {
  const { t } = useTranslation("feed");
  const fmt = useFormat();
  const {
    captureFlock, setCaptureFlock,
    captureFlockGen, setCaptureFlockGen,
    captureFlockSnapshot, setCaptureFlockSnapshot,
    capturePickerOpen, setCapturePickerOpen,
    itemId, setItemId,
    date, setDate,
    quantity, setQuantity,
    note, setNote,
  } = fields;
  return (
    <>
      {isDesktop && <ConsoleSummary label={t("contextLabel")} items={[
        { label: t("flockLabel"), value: captureFlock?.name ?? "—" },
        { label: t("itemLabel"), value: selectedItem?.name ?? "—" },
        { label: t("onHand"), value: selectedItem ? `${fmt.count(selectedItem.quantityOnHand)} ${selectedItem.unit}` : "—" },
      ]} />}
      <Box sx={isDesktop ? CONSOLE_TICKET_SX : { ...CONSOLE_TICKET_SX, border: 0, my: 0 }}>
        <Stack component="form" id="feed-entry-form" sx={{ ...CONSOLE_TICKET_FORM_SX, p: isDesktop ? 2 : 0 }} onSubmit={onSubmit}>
          <Box sx={{ minWidth: 0, width: "100%" }}>
            <FlockPicker
              label={t("flockLabel")}
              eligibility="active-and-depleted"
              required
              open={capturePickerOpen}
              controlledCommitted={captureFlock}
              controlledGeneration={captureFlockGen}
              onSnapshot={setCaptureFlockSnapshot}
              onCommit={(f) => {
                setCaptureFlock(f);
                setCaptureFlockGen((g) => g + 1);
                setCapturePickerOpen(false);
              }}
              onEscape={() => setCapturePickerOpen(false)}
              onOutsideClick={() => setCapturePickerOpen(false)}
              trigger={
                <button
                  type="button"
                  className="named-picker-trigger"
                  onClick={() => setCapturePickerOpen(true)}
                >
                  {captureFlock
                    ? `${captureFlock.name}${captureFlock.status === "Depleted" ? t("depletedFlockSuffix") : ""}`
                    : t("selectFlockOption")}
                </button>
              }
            />
          </Box>
          <TextField
            select
            label={t("itemLabel")}
            value={itemId}
            size="small"
            slotProps={{ select: { native: true } }}
            onChange={(e) => setItemId(e.target.value)}
          >
            {pickableItems.map((x) => (
              <option key={x.id} value={x.id}>
                {t("itemOption", { name: x.name, onHand: x.quantityOnHand, unit: x.unit })}
                {x.active ? ""
                  : x.quantityOnHand > 0 ? t("inactiveItemSuffix")
                    : t("inactiveEmptyItemSuffix")}
              </option>
            ))}
          </TextField>
          <TextField
            type="date"
            label={t("dateLabel")}
            value={date}
            size="small"
            slotProps={{ htmlInput: { max: today, required: true }, inputLabel: { shrink: true } }}
            onChange={(e) => setDate(e.target.value)}
          />
          <TextField
            type="number"
            label={selectedItem
              ? t("quantityLabelWithUnit", { unit: selectedItem.unit })
              : t("quantityLabel")}
            value={quantity}
            size="small"
            slotProps={{ htmlInput: { min: 0.001, step: 0.001, required: true } }}
            onChange={(e) => setQuantity(e.target.value)}
          />
          <TextField
            label={t("noteLabel")}
            sx={{ gridColumn: { md: "span 3" } }}
            value={note}
            size="small"
            slotProps={{ htmlInput: { maxLength: 500 } }}
            onChange={(e) => setNote(e.target.value)}
          />
          {isDesktop && <BusyButton variant="contained" type="submit" busy={busy}
            disabled={!captureFlock || !captureFlockSnapshot.canSubmit || !itemId}>
            {t("recordFeedButton")}
          </BusyButton>}
        </Stack>

        <Box component="aside" aria-label={t("rationCheck")} sx={isDesktop ? CONSOLE_TICKET_CHECK_SX : { ...CONSOLE_TICKET_CHECK_SX, p: 1.5, mt: 2, border: "1px solid var(--rule)", borderRadius: "5px" }}>
          <h3>{t("rationCheck")}</h3>
          <Box component="dl" sx={{ m: 0 }}>
            {([
              ["onHand", selectedItem ? `${fmt.count(selectedItem.quantityOnHand)} ${selectedItem.unit}` : "—"],
              ["issue", selectedItem && quantity !== "" ? `${fmt.count(Number(quantity))} ${selectedItem.unit}` : "—"],
              ["afterIssue", selectedItem && afterIssueQuantity !== null ? `${fmt.count(afterIssueQuantity)} ${selectedItem.unit}` : "—"],
            ] as const).map(([key, value]) => <Box key={key}
              aria-live={key === "afterIssue" ? "polite" : undefined}
              aria-atomic={key === "afterIssue" ? true : undefined}
              sx={{ display: "flex", justifyContent: "space-between", gap: 2, py: .5, borderBottom: "1px solid var(--rule)" }}>
              <Typography component="dt" sx={{ fontSize: ".8rem" }}>{t(key)}</Typography>
              <Typography component="dd" variant="body2" sx={{ m: 0, fontSize: ".8rem", fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>{value}</Typography>
            </Box>)}
          </Box>
          <Box component="p" className="muted" sx={{ mt: 1, mb: 0 }}>{t("correctionsHint")}</Box>
        </Box>
      </Box>

      {error && <p className="error">{error}</p>}
      {isDesktop && message && <p className="success">{message}</p>}
    </>
  );
}
