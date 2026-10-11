import type { Dispatch, FormEvent, SetStateAction } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Plus } from "lucide-react";
import {
  Box, Button, DialogActions, List, ListItem, Stack, TextField, Typography,
} from "@mui/material";
import { recordInventoryAdjustment, recordInventoryPurchase } from "../api/cluckwork";
import type { InventoryItem, InventoryLot, InventoryMovement } from "../api/cluckwork";
import { useFormat } from "../farm/useFormat";
import { FarmDate } from "../components/FarmDate";
import { CONSOLE_LINK_SX, CONSOLE_RAIL_SX } from "../components/FieldConsole";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { DialogError } from "../components/DialogError";
import type { DialogErrors } from "../components/useDialogErrors";
import { usePagedList } from "../components/usePagedList";
import { FEEDABLE_CATEGORIES } from "./FeedPage";
import i18n from "../i18n";
import { inventoryCategoryLabel, inventoryMovementLabel } from "../i18n/enums";

// The open item's panel (F131): its purchase and stock-correction dialogs with
// their submit handlers, and the movement ledger. Dialog state, the lots read
// and the write discipline stay on InventoryPage — this renders from it, so
// the dialogs share the page's one useDialogAction session and message slots
// (#703) and each renders its own failure through its own slot (#479).
export function InventoryItemPanel({
  active,
  isAdmin,
  busy,
  isPending,
  errors,
  run,
  commit,
  openDialog,
  purchasing, setPurchasing,
  adjusting, setAdjusting,
  closePurchase,
  closeAdjust,
  lots,
  setAdjustLotId,
  adjustLotId,
  adjustType, setAdjustType,
  adjustQty, setAdjustQty,
  adjustReason, setAdjustReason,
  purchaseDate, setPurchaseDate,
  purchaseQty, setPurchaseQty,
  purchaseCost, setPurchaseCost,
  lotNumber, setLotNumber,
  expiryDate, setExpiryDate,
  purchaseNote, setPurchaseNote,
  today,
  costStep,
  toMinorUnits,
  ledger,
  setActive,
  setMessage,
}: {
  active: InventoryItem;
  isAdmin: boolean;
  busy: boolean;
  isPending: (scope: string) => boolean;
  errors: DialogErrors;
  run: <T>(scope: string, action: (current: () => boolean) => Promise<T>) => Promise<T | undefined>;
  commit: (keyScope: string, action: (key: string) => Promise<unknown>, touchesLedger?: boolean) => Promise<void>;
  openDialog: (scope: "purchase" | "adjust") => void;
  purchasing: boolean; setPurchasing: Dispatch<SetStateAction<boolean>>;
  adjusting: boolean; setAdjusting: Dispatch<SetStateAction<boolean>>;
  closePurchase: () => void;
  closeAdjust: () => void;
  lots: InventoryLot[];
  setAdjustLotId: Dispatch<SetStateAction<string>>;
  adjustLotId: string;
  adjustType: string; setAdjustType: Dispatch<SetStateAction<string>>;
  adjustQty: string; setAdjustQty: Dispatch<SetStateAction<string>>;
  adjustReason: string; setAdjustReason: Dispatch<SetStateAction<string>>;
  purchaseDate: string; setPurchaseDate: Dispatch<SetStateAction<string>>;
  purchaseQty: string; setPurchaseQty: Dispatch<SetStateAction<string>>;
  purchaseCost: string; setPurchaseCost: Dispatch<SetStateAction<string>>;
  lotNumber: string; setLotNumber: Dispatch<SetStateAction<string>>;
  expiryDate: string; setExpiryDate: Dispatch<SetStateAction<string>>;
  purchaseNote: string; setPurchaseNote: Dispatch<SetStateAction<string>>;
  today: string;
  costStep: number;
  toMinorUnits: (text: string) => number | null;
  ledger: ReturnType<typeof usePagedList<InventoryMovement>>;
  setActive: Dispatch<SetStateAction<InventoryItem | null>>;
  setMessage: Dispatch<SetStateAction<string | null>>;
}) {
  const { t } = useTranslation("inventory");
  const { t: tc } = useTranslation("common");
  const fmt = useFormat();

  const canFeed = active !== null && FEEDABLE_CATEGORIES.includes(active.category);

  async function onPurchase(e: FormEvent) {
    e.preventDefault();
    if (!active) return;
    // #703 review r2 (PR 2) — a skipped-while-busy submit must not un-mute:
    // Enter bypasses the disabled submit button, `run` would skip the second
    // attempt, but the pre-run `beginAttempt` below would still un-mute an
    // abandoned attempt and let its late failure into the reopened dialog.
    if (busy) return;
    // Clears the purchase slot whether or not the check below fails, so a
    // fixed keystroke doesn't leave a stale verdict behind.
    errors.beginAttempt("purchase");
    const qty = parseFloat(purchaseQty);
    if (!Number.isFinite(qty) || qty <= 0) {
      errors.report("purchase", i18n.t("inventory:quantityMustBePositive"));
      return;
    }
    // The run scope is the dialog's; the idempotency key stays per item.
    await run("purchase", async (current) => {
      await commit(`purchase:${active.id}`, (key) =>
        recordInventoryPurchase(active.id, {
          receivedDate: purchaseDate,
          quantity: qty,
          unitCostMinorUnits: toMinorUnits(purchaseCost),
          lotNumber: lotNumber.trim() || undefined,
          expiryDate: expiryDate || undefined,
          note: purchaseNote.trim() || undefined,
        }, key), true);
      // Superseded (#703): the lot exists, the items/lots/ledger show it and
      // the key rotated; the resets, the message and the close are the
      // replacement session's.
      if (!current()) return;
      setPurchaseQty("");
      setPurchaseCost("");
      setLotNumber("");
      setExpiryDate("");
      setPurchaseNote("");
      setMessage(i18n.t("inventory:purchaseRecordedMessage"));
      setPurchasing(false);
    });
  }

  async function onAdjust(e: FormEvent) {
    e.preventDefault();
    if (!active) return;
    // Same reasoning as onPurchase: a skipped-while-busy submit must not
    // un-mute (#703 review r2), and the slot is cleared up front so either
    // guard below reports against a clean slot.
    if (busy) return;
    errors.beginAttempt("adjust");
    const delta = parseFloat(adjustQty);
    if (!Number.isFinite(delta) || delta === 0) {
      errors.report("adjust", i18n.t("inventory:adjustQuantityRequired"));
      return;
    }
    if (!adjustReason.trim()) {
      errors.report("adjust", i18n.t("inventory:adjustReasonRequired"));
      return;
    }
    // The run scope is the dialog's; the idempotency key stays per item + lot.
    await run("adjust", async (current) => {
      await commit(`adjust:${active.id}:${adjustLotId}`, (key) =>
        recordInventoryAdjustment(active.id, {
          inventoryLotId: adjustLotId,
          date: today,
          type: adjustType,
          quantityDelta: adjustType === "Discard" ? -Math.abs(delta) : delta,
          reason: adjustReason.trim(),
        }, key), true);
      // Superseded (#703): the correction landed and the view shows it; the
      // resets, the message and the close are the replacement session's.
      if (!current()) return;
      setAdjustQty("");
      setAdjustReason("");
      setMessage(i18n.t("inventory:correctionRecordedMessage"));
      setAdjusting(false);
    });
  }

  const lotLabel = (l: InventoryLot) =>
    `${fmt.date(l.receivedDate)}${l.lotNumber ? ` · ${l.lotNumber}` : ""} — ${fmt.count(l.quantityAvailable)}/${fmt.count(l.quantityReceived)}`;

  return (
    <Box sx={{ my: 3 }}>
      <Box sx={{ py: 1 }}>
        <Box sx={{ ...CONSOLE_RAIL_SX, display: "grid", gridTemplateColumns: { xs: "minmax(0, 1fr)", md: "minmax(0, 1fr) auto minmax(250px, 1fr)" }, gap: 2, alignItems: "center" }}>
          <Typography variant="h3" component="h3" aria-label={t("itemPanelHeading", { name: active.name, quantity: active.quantityOnHand, unit: active.unit })}>
            {active.name}
          </Typography>
          <Box>
            <Typography sx={{ fontSize: ".75rem" }}>{t("onHandHeader")}</Typography>
            <Typography variant="body2" sx={{ fontSize: "2rem", fontFamily: "Georgia, serif", fontVariantNumeric: "tabular-nums" }}>{fmt.count(active.quantityOnHand)} {active.unit}</Typography>
          </Box>
          <Box sx={{ display: "grid", gridTemplateColumns: "repeat(2, minmax(0, 1fr))", gap: 1, "& button, & a": { height: 44, minHeight: 44, p: 1, fontSize: { xs: ".7rem", md: ".8rem" }, lineHeight: 1.2 } }}>
            <Button variant="outlined" sx={{ bgcolor: "#fff", color: "#2c2429", borderColor: "#fff" }} onClick={() => { openDialog("purchase"); setPurchasing(true); }}>
              <Plus size={16} aria-hidden /> {t("recordPurchaseButton")}
            </Button>
            {canFeed && (
              <Button component={Link} variant="outlined" sx={{ bgcolor: "#fff", color: "#2c2429", borderColor: "#fff" }} to={`/feed?item=${active.id}`}>
                {t("recordUsageLink")}
              </Button>
            )}
            {isAdmin && lots.length > 0 && (
              <Button variant="contained" sx={{ gridColumn: "1 / -1" }} onClick={() => { openDialog("adjust"); setAdjusting(true); }}>
                {t("correctStockButton")}
              </Button>
            )}
          </Box>
        </Box>

        {!canFeed && (
          <p className="muted">
            {t("notFeedableMessage", { category: inventoryCategoryLabel(active.category) })}
          </p>
        )}
        {!isAdmin ? (
          <p className="muted">{t("correctionsNeedAdminMessage")}</p>
        ) : lots.length === 0 ? (
          <p className="muted">{t("noLotsMessage")}</p>
        ) : null}

        <Dialog open={purchasing} title={t("recordPurchaseDialogTitle", { name: active.name })} onClose={closePurchase}
          actions={(
            <DialogActions>
              <button type="button" className="link" onClick={closePurchase}>{tc("cancel")}</button>
              <BusyButton variant="contained" type="submit" busy={isPending("purchase")} disabled={busy}>
                {t("recordPurchaseSubmitButton")}
              </BusyButton>
            </DialogActions>
          )}
          formProps={{ onSubmit: onPurchase }}
        >
          <Stack spacing={2}>
            <TextField
              type="date"
              label={t("receivedLabel")}
              value={purchaseDate}
              slotProps={{ htmlInput: { max: today, required: true }, inputLabel: { shrink: true } }}
              onChange={(e) => setPurchaseDate(e.target.value)}
            />
            <TextField
              type="number"
              label={t("quantityLabelWithUnit", { unit: active.unit })}
              value={purchaseQty}
              slotProps={{ htmlInput: { min: 0.001, step: 0.001, required: true } }}
              onChange={(e) => setPurchaseQty(e.target.value)}
            />
            <TextField
              type="number"
              label={active.defaultCostCurrencyCode
                ? t("unitCostWithCurrencyLabel", { code: active.defaultCostCurrencyCode })
                : t("unitCostLabel")}
              value={purchaseCost}
              placeholder={active.defaultCostMinorUnits !== null ? t("costPlaceholderItemDefault") : t("costPlaceholderRequired")}
              slotProps={{ htmlInput: { min: 0, step: costStep } }}
              onChange={(e) => setPurchaseCost(e.target.value)}
            />
            <TextField
              label={t("lotNumberLabel")}
              value={lotNumber}
              slotProps={{ htmlInput: { maxLength: 100 } }}
              onChange={(e) => setLotNumber(e.target.value)}
            />
            <TextField
              type="date"
              label={t("expiryLabel")}
              value={expiryDate}
              slotProps={{ htmlInput: { min: purchaseDate }, inputLabel: { shrink: true } }}
              onChange={(e) => setExpiryDate(e.target.value)}
            />
            <TextField
              label={t("noteLabel")}
              value={purchaseNote}
              slotProps={{ htmlInput: { maxLength: 500 } }}
              onChange={(e) => setPurchaseNote(e.target.value)}
            />
            <DialogError errors={errors} scope="purchase" />
          </Stack>
        </Dialog>

        <Dialog open={adjusting && isAdmin} title={t("correctStockDialogTitle", { name: active.name })} onClose={closeAdjust}
          actions={(
            <DialogActions>
              <button type="button" className="link" onClick={closeAdjust}>{tc("cancel")}</button>
              <BusyButton variant="contained" type="submit" busy={isPending("adjust")}
                disabled={busy || !adjustLotId}>
                {t("recordCorrectionButton")}
              </BusyButton>
            </DialogActions>
          )}
          formProps={{ onSubmit: onAdjust }}
        >
          <Stack spacing={2}>
            <TextField
              select
              label={t("lotFieldLabel")}
              value={adjustLotId}
              disabled={busy}
              slotProps={{ select: { native: true } }}
              onChange={(e) => setAdjustLotId(e.target.value)}
            >
              {lots.map((l) => <option key={l.id} value={l.id}>{lotLabel(l)}</option>)}
            </TextField>
            <TextField
              select
              label={t("typeLabel")}
              value={adjustType}
              slotProps={{ select: { native: true } }}
              onChange={(e) => setAdjustType(e.target.value)}
            >
              <option value="Adjustment">{t("adjustTypeAdjustmentOption")}</option>
              <option value="Discard">{t("adjustTypeDiscardOption")}</option>
            </TextField>
            <TextField
              type="number"
              label={t("quantityLabelWithUnit", { unit: active.unit })}
              value={adjustQty}
              placeholder={adjustType === "Discard" ? t("adjustQuantityPlaceholderDiscard") : t("adjustQuantityPlaceholderCorrection")}
              slotProps={{ htmlInput: { step: 0.001, required: true } }}
              onChange={(e) => setAdjustQty(e.target.value)}
            />
            <TextField
              label={t("reasonLabel")}
              value={adjustReason}
              slotProps={{ htmlInput: { maxLength: 500, required: true } }}
              onChange={(e) => setAdjustReason(e.target.value)}
            />
            <DialogError errors={errors} scope="adjust" />
          </Stack>
        </Dialog>

        <Stack direction="row" sx={{ justifyContent: "space-between", alignItems: "center", gap: 1, mb: 1 }}>
          <Typography component="h3" variant="h3" sx={{ "&&": { m: 0 }, minWidth: 0 }}>{t("movementHeading")}</Typography>
          <Button size="small" color="inherit" sx={{ ...CONSOLE_LINK_SX, minHeight: 44, flexShrink: 0, fontSize: ".75rem" }} onClick={() => setActive(null)}>{t("chooseAnotherItem")}</Button>
        </Stack>
        {ledger.error && <p className="error">{ledger.error}</p>}
        {ledger.rows === null || ledger.reloading ? (
          <p className="muted">{tc("loading")}</p>
        ) : ledger.rows.length === 0 && !ledger.error ? (
          <p className="muted">{t("noMovementsMessage")}</p>
        ) : (
          <List aria-label={t("movementHeading")} disablePadding>
            {ledger.rows.map((m) => (
              <ListItem key={m.id} sx={{ display: "grid", gridTemplateColumns: { xs: "72px 75px 70px minmax(0, 1fr)", md: "86px 90px 90px minmax(140px, 1fr)" }, gap: "10px", p: "10px 8px", borderBottom: "1px solid var(--rule)", fontSize: "12px", alignItems: "start", "& > *": { minWidth: 0, overflowWrap: "anywhere" } }}>
                <FarmDate iso={m.date} />
                <strong>{inventoryMovementLabel(m.type)}</strong>
                <Box component="span" sx={{ color: m.quantityDelta > 0 ? "var(--success)" : "var(--error)", fontWeight: 800 }}>{m.quantityDelta > 0 ? `+${fmt.count(m.quantityDelta)}` : fmt.count(m.quantityDelta)} {m.unit}</Box>
                <span>{m.note ?? ""}</span>
              </ListItem>
            ))}
          </List>
        )}
        {ledger.canLoadMore && (
          <button className="link" onClick={() => void ledger.loadMore()}>
            {t("loadMoreButton")}
          </button>
        )}
        <Typography component="p" sx={{ fontSize: ".7rem", color: "text.secondary", mt: 1 }}>{t("movementPolicy")}</Typography>
      </Box>
    </Box>
  );
}
