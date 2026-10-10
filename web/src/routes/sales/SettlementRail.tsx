import type { ReactNode } from "react";
import { Box, Button, Typography } from "@mui/material";
import { useTranslation } from "react-i18next";
import type { SalesOrder } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { CONSOLE_RAIL_SX } from "../../components/FieldConsole";
import { useFormat } from "../../farm/useFormat";
import { discountReasonLabel } from "../../i18n/enums";
import { lineExceedsCeiling, type DiscountCeiling } from "../../lib/discountCeiling";
import { orderDiscount, orderIsAtList, orderListPriceBasis, orderListValue } from "./orderMath";
import { useDiscountPercent, type SalesAction } from "./salesUi";
import type { ActiveOrderState } from "./useActiveOrder";

export function SettlementRail({
  active, activeOrder, ceiling, ceilingPercent, canSettle, isAdmin, action, paymentsPanel,
  onCancel, onConfirm, onVoid, closeOrderPanel,
}: {
  active: SalesOrder;
  activeOrder: ActiveOrderState;
  ceiling: DiscountCeiling | null;
  ceilingPercent: string;
  canSettle: boolean;
  isAdmin: boolean;
  action: SalesAction;
  paymentsPanel: ReactNode;
  onCancel: () => Promise<void>;
  onConfirm: () => Promise<void>;
  onVoid: () => Promise<void>;
  closeOrderPanel: () => void;
}) {
  const { t } = useTranslation("sales");
  const fmt = useFormat();
  const discountPercent = useDiscountPercent();
  const { busy, isPending } = action;
  const { editor, editingLine } = activeOrder;
  // #727 — what blocks Confirm. Reads the SAVED lines, deliberately unlike the
  // row badge, which tracks the price being typed: Confirm posts what the
  // server already holds, so an unsaved edit must neither block a confirm that
  // would succeed nor permit one that would not.
  const orderOverCeiling = active !== null && ceiling !== null
    && active.items.some((i) =>
      lineExceedsCeiling(i.listUnitPriceMinorUnits, i.unitPriceMinorUnits, ceiling));
  const listValue = orderListValue(active.items);
  return (
    <Box component="aside" aria-label={t("settlementHeading")} sx={{
      ...CONSOLE_RAIL_SX,
      "--surface": "#2c2429",
      "--surface-2": "#433840",
      "& .muted, & .discount-note": { color: "#cfc4cb" },
      "& .discount, & .warn": { color: "#ffcf85", fontWeight: 750 },
      "& .MuiTableCell-root": { color: "inherit" },
      "& .MuiTableContainer-root": { background: "var(--surface)", backgroundImage: "none" },
      "& p:has(+ .MuiTableContainer-root)": { bgcolor: "var(--surface-2)", color: "inherit" },
      "& .actions": { alignItems: "stretch" },
      "& .actions:not([role=group])": { flexDirection: "column" },
      "& .actions > button": { minHeight: 44 },
    }}>
      <Typography component="p" variant="body2" aria-label={t("orderTotal", { amount: fmt.money(active.totalMinorUnits, active.currencyCode, active.currencyMinorUnit) })} sx={{ mt: 0, mb: 2 }}>
        <Box component="span" sx={{ display: "block", fontSize: ".8rem" }}>{t("settlementHeading")}</Box>
        <Box component="strong" sx={{ display: "block", fontSize: "1.75rem", fontFamily: 'Georgia, "Times New Roman", serif', fontVariantNumeric: "tabular-nums" }}>{fmt.money(active.totalMinorUnits, active.currencyCode, active.currencyMinorUnit)}</Box>
      </Typography>
      <Box component="dl" sx={{ m: 0,
        "& > div": { display: "grid", gridTemplateColumns: "auto minmax(0, 1fr)", gap: 1.5, py: 1, borderTop: "1px solid var(--rule)", fontSize: ".8rem" },
        "& dd": { m: 0, textAlign: "right", fontVariantNumeric: "tabular-nums" },
        "& dd p": { m: 0 },
      }}>
        <Box>
          <Box component="dt">{t("listValue")}</Box>
          <Box component="dd" aria-label={t("listValue")} title={listValue === null ? t("listValueIncomplete") : undefined}>
            {listValue === null ? "—" : fmt.money(listValue, active.currencyCode, active.currencyMinorUnit)}
          </Box>
        </Box>
        <Box>
          <Box component="dt">{t("discount")}</Box>
          <Box component="dd">
            {(() => {
              const orderLevel = orderDiscount(active.items);
              if (active.items.length === 0) return <span>—</span>;
              // Unpriced lines make an otherwise at-list order only partially measurable.
              if (orderLevel.kind === "atList" && orderLevel.partial) {
                return (
                  <p className="discount-note" data-testid="order-discount-partial">
                    {t("discountPartialOnly")}
                  </p>
                );
              }

              if (orderLevel.kind === "unknown") {
                return (
                  <p className="discount-note" data-testid="order-discount-unknown">
                    {{
                      allPreDating: t("discountUnrecordedOrder"),
                      nonePreDating: t("discountUnknownOrder"),
                      mixed: t("discountPartlyUnrecordedOrder"),
                    }[orderListPriceBasis(active.items)]}
                  </p>
                );
              }
              if (orderLevel.kind !== "below") {
                return <span>{orderIsAtList(active.items) ? t("atListShort") : t("aboveList")}</span>;
              }
              const amount = fmt.money(orderLevel.amountMinorUnits, active.currencyCode, active.currencyMinorUnit);
              return (
                <p className="discount" data-testid="order-discount">
                  {orderLevel.percent === null
                    ? t("discountTotalNoPct", { amount })
                    : t("discountTotal", { amount, percent: discountPercent(orderLevel.percent) })}
                  {orderLevel.partial ? ` (${t("discountPartialNote")})` : ""}
                </p>
              );
            })()}
          </Box>
        </Box>
        {(active.status === "Draft" || active.status === "Confirmed") && <Box>
          <Box component="dt">{t("stockCommitment")}</Box>
          <Box component="dd">{t("eggsCount", {
              count: active.items.reduce((sum, item) => sum + (
                editor && editingLine?.id === item.id
                  ? editor.quantity * item.baseUnitFactor
                  : item.quantityBase
              ), 0),
            })}</Box>
        </Box>}
      </Box>
      {/* Older orders have no backfilled discount reason; absence means it was not recorded. */}
      {active.discountReasonCode && (
        <p className="discount-note" data-testid="order-discount-reason">
          {active.discountReasonNote
            ? t("discountReasonSummaryWithNote", {
                reason: discountReasonLabel(active.discountReasonCode),
                note: active.discountReasonNote,
              })
            : t("discountReasonSummary", {
                reason: discountReasonLabel(active.discountReasonCode),
              })}
        </p>
      )}

      {active.status === "Draft" && (
        <>
          {/* Keep Confirm enabled: the server checks the ceiling, which may differ from this cache. */}
          {orderOverCeiling && (
            <p className="warn" role="status" data-testid="order-ceiling-warning">
              {t("discountCeilingWarning", { percent: ceilingPercent })}
            </p>
          )}
          <Box className="actions" role="group" aria-label={t("draftActions")} sx={{
            "&&": { flexDirection: "row", flexWrap: "nowrap", justifyContent: "flex-end", gap: 1 },
            "& > button": { flex: { xs: "1 1 50%", md: "0 1 auto" }, borderRadius: "4px", boxSizing: "border-box", minWidth: 0, minHeight: 44, px: 1, fontSize: ".8rem" },
          }}>
            <BusyButton variant="outlined" sx={{ bgcolor: "common.white", color: "var(--surface)", borderColor: "common.white", "&:hover": { bgcolor: "common.white", borderColor: "common.white" } }} disabled={busy} busy={isPending(`cancel:${active.id}`)}
              onClick={() => void onCancel()}>{t("cancelDraft")}</BusyButton>
            <BusyButton variant="contained" disabled={busy || active.items.length === 0}
              busy={isPending(`confirm:${active.id}`)} onClick={() => void onConfirm()}>
              {t("confirmOrderButton")}
            </BusyButton>
          </Box>
          <Button variant="outlined" color="inherit" fullWidth sx={{ minHeight: 44, mt: 1 }} onClick={closeOrderPanel}>{t("close")}</Button>
        </>
      )}
      {active.status === "Confirmed" && canSettle && paymentsPanel}
      {active.status === "Voided" && active.voidReason && (
        <p className="muted">{t("voidReasonLabel", { reason: active.voidReason })}</p>
      )}
      {active.status !== "Draft" && (
        <div className="actions">
          {active.status === "Confirmed" && isAdmin && (
            <BusyButton variant="outlined" sx={{ color: "#ffb4a2", borderColor: "currentColor" }} disabled={busy} busy={isPending(`void:${active.id}`)}
              onClick={() => void onVoid()}>
              {t("voidOrderButton")}
            </BusyButton>
          )}
          {active.status === "Confirmed" && !isAdmin && (
            <span className="muted">{t("voidingNeedsAdmin")}</span>
          )}
          <Button variant="outlined" color="inherit" onClick={closeOrderPanel}>{t("close")}</Button>
        </div>
      )}
    </Box>
  );
}
