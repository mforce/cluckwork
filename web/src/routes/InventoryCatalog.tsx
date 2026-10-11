import { useRef } from "react";
import type { Dispatch, RefObject, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { Box, Button, Table, TableBody, TableCell, TableHead, TableRow } from "@mui/material";
import { activateInventoryItem, deactivateInventoryItem, listInventoryLots } from "../api/cluckwork";
import type { InventoryItem } from "../api/cluckwork";
import { useFormat } from "../farm/useFormat";
import { LedgerTableContainer, CONSOLE_LINK_SX } from "../components/FieldConsole";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { StatusBadge } from "../components/StatusBadge";
import { PhoneDetailsField, PhoneLedgerList, PhoneLedgerRow, PhoneLedgerSummary } from "../components/PhoneLedger";
import { inventoryCategoryLabel, statusLabel } from "../i18n/enums";

const NOWRAP = { whiteSpace: "nowrap" as const };

// The items catalog listing: the desktop table, and the phone ledger list with
// its read-only peek (#987). Rows render from InventoryPage's state — the
// peek's own open-lot ticket travels with `openDetails`.
export function InventoryCatalog({
  items,
  isDesktop,
  isAdmin,
  busy,
  isPending,
  run,
  commit,
  onOpen,
  startEdit,
  minorUnit,
  detailsId,
  setDetailsId,
  detailsOpenLots,
  setDetailsOpenLots,
  focusAfterWrite,
}: {
  items: InventoryItem[];
  isDesktop: boolean;
  isAdmin: boolean;
  busy: boolean;
  isPending: (scope: string) => boolean;
  run: <T>(scope: string, action: (current: () => boolean) => Promise<T>) => Promise<T | undefined>;
  commit: (keyScope: string, action: (key: string) => Promise<unknown>, touchesLedger?: boolean) => Promise<void>;
  onOpen: (i: InventoryItem) => Promise<void>;
  startEdit: (i: InventoryItem) => void;
  minorUnit: number;
  detailsId: string | null;
  setDetailsId: Dispatch<SetStateAction<string | null>>;
  detailsOpenLots: number | null;
  setDetailsOpenLots: Dispatch<SetStateAction<number | null>>;
  focusAfterWrite: RefObject<string | null>;
}) {
  const { t } = useTranslation("inventory");
  const fmt = useFormat();
  const detailsLotsRequest = useRef(0);
  const details = items.find((i) => i.id === detailsId) ?? null;

  function openDetails(i: InventoryItem) {
    setDetailsId(i.id);
    setDetailsOpenLots(null);
    const req = ++detailsLotsRequest.current;
    listInventoryLots(i.id)
      .then((rows) => {
        if (detailsLotsRequest.current === req) {
          setDetailsOpenLots(rows.filter((l) => l.quantityAvailable > 0).length);
        }
      })
      .catch(() => {});
  }

  const costText = (i: InventoryItem) =>
    i.defaultCostMinorUnits !== null && i.defaultCostCurrencyCode
      ? fmt.money(i.defaultCostMinorUnits, i.defaultCostCurrencyCode,
          i.defaultCostCurrencyMinorUnit ?? minorUnit)
      : "—";

  return (
    <>
      {isDesktop ? <LedgerTableContainer>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>{t("nameHeader")}</TableCell>
              <TableCell>{t("categoryHeader")}</TableCell>
              <TableCell align="right">{t("onHandHeader")}</TableCell>
              <TableCell align="right">{t("defaultCostHeader")}</TableCell>
              <TableCell>{t("statusHeader")}</TableCell>
              <TableCell></TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {items.map((i) => (
              <TableRow key={i.id} sx={i.active ? undefined : { "& .MuiTableCell-root": { color: "var(--muted)" } }}>
                <TableCell>{i.name}</TableCell>
                <TableCell>{inventoryCategoryLabel(i.category)}</TableCell>
                <TableCell align="right">{fmt.count(i.quantityOnHand)} {i.unit}</TableCell>
                <TableCell align="right">{costText(i)}</TableCell>
                <TableCell><StatusBadge status={i.active ? "Active" : "Inactive"} label={statusLabel(i.active ? "Active" : "Inactive")} /></TableCell>
                <TableCell sx={NOWRAP}>
                  <button className="link" disabled={busy} onClick={() => void onOpen(i)}>{t("openButton")}</button>
                  {isAdmin && (
                    <>
                      <button className="link" disabled={busy}
                        onClick={() => startEdit(i)}>{t("editButton")}</button>
                      {i.active ? (
                        <BusyButton variant="text" sx={CONSOLE_LINK_SX} busy={isPending(`deactivate:${i.id}`)} disabled={busy}
                          onClick={() => void run(`deactivate:${i.id}`, () => commit(`deactivate:${i.id}`, (key) => deactivateInventoryItem(i.id, key)))}>
                          {t("deactivateButton")}
                        </BusyButton>
                      ) : (
                        <BusyButton variant="text" sx={CONSOLE_LINK_SX} busy={isPending(`activate:${i.id}`)} disabled={busy}
                          onClick={() => void run(`activate:${i.id}`, () => commit(`activate:${i.id}`, (key) => activateInventoryItem(i.id, key)))}>
                          {t("activateButton")}
                        </BusyButton>
                      )}
                    </>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </LedgerTableContainer> : <>
        <PhoneLedgerList label={t("itemsHeading")}>
          {items.map((i) => <li key={i.id}>
            {/* No date, so the name takes the flexible slot and stops wrapping (#987). */}
            <PhoneLedgerRow rowId={i.id} muted={!i.active} disabled={busy} onClick={() => openDetails(i)}
              primary={i.name}
              trailing={<strong>{fmt.count(i.quantityOnHand)} {i.unit}</strong>}
              summary={<PhoneLedgerSummary parts={[
                <strong>{inventoryCategoryLabel(i.category)}</strong>,
                i.defaultCostMinorUnits === null ? null : t("costPerUnitShort", { cost: costText(i), unit: i.unit }),
                !i.active && <Box component="span" sx={{ color: "var(--error)" }}>{statusLabel("Inactive")}</Box>,
              ]} />} />
          </li>)}
        </PhoneLedgerList>
        {/* Every action closes the peek first, so the page's own error and
            success lines are visible when the write answers. */}
        <Dialog open={details !== null} compactTitle title={details ? details.name : t("title")}
          onClose={() => setDetailsId(null)}
          actions={details && <Box sx={{ display: "flex", alignItems: "center", gap: 1, borderTop: "1px solid var(--rule)", "& .MuiButtonBase-root": { minHeight: 44 } }}>
            {isAdmin && <>
              <Button size="small" sx={CONSOLE_LINK_SX} disabled={busy}
                onClick={() => { const item = details; setDetailsId(null); startEdit(item); }}>{t("editButton")}</Button>
              {details.active ? (
                <BusyButton variant="text" sx={CONSOLE_LINK_SX} busy={isPending(`deactivate:${details.id}`)} disabled={busy}
                  onClick={() => {
                    const { id } = details;
                    focusAfterWrite.current = id;
                    setDetailsId(null);
                    void run(`deactivate:${id}`, () => commit(`deactivate:${id}`, (key) => deactivateInventoryItem(id, key)));
                  }}>{t("deactivateButton")}</BusyButton>
              ) : (
                <BusyButton variant="text" sx={CONSOLE_LINK_SX} busy={isPending(`activate:${details.id}`)} disabled={busy}
                  onClick={() => {
                    const { id } = details;
                    focusAfterWrite.current = id;
                    setDetailsId(null);
                    void run(`activate:${id}`, () => commit(`activate:${id}`, (key) => activateInventoryItem(id, key)));
                  }}>{t("activateButton")}</BusyButton>
              )}
            </>}
            <Button variant="contained" sx={{ ml: "auto", borderRadius: "4px" }} disabled={busy}
              onClick={() => { const item = details; setDetailsId(null); void onOpen(item); }}>{t("openButton")}</Button>
          </Box>}>
          {details && <Box component="dl" sx={{ m: 0 }}>
            <PhoneDetailsField label={t("categoryHeader")}>{inventoryCategoryLabel(details.category)}</PhoneDetailsField>
            <PhoneDetailsField label={t("onHandHeader")}>{fmt.count(details.quantityOnHand)} {details.unit}</PhoneDetailsField>
            <PhoneDetailsField label={t("defaultCostHeader")}>
              {details.defaultCostMinorUnits === null ? "—" : t("costPerUnitShort", { cost: costText(details), unit: details.unit })}
            </PhoneDetailsField>
            <PhoneDetailsField label={t("unitHeader")}>{details.unit}</PhoneDetailsField>
            <PhoneDetailsField label={t("lotsHeader")}>
              {detailsOpenLots === null ? "—" : t("lotsOpenValue", { lots: fmt.count(detailsOpenLots) })}
            </PhoneDetailsField>
            <PhoneDetailsField label={t("statusHeader")}>
              <StatusBadge status={details.active ? "Active" : "Inactive"} label={statusLabel(details.active ? "Active" : "Inactive")} />
            </PhoneDetailsField>
          </Box>}
        </Dialog>
      </>}
    </>
  );
}
