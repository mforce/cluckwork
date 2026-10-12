import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Box, Table, TableBody, TableCell, TableHead, TableRow } from "@mui/material";
import type { EggUnitConversion } from "../api/cluckwork";
import {
  LedgerTableContainer, ListInspectorPane, RecordInspector, STICKY_TABLE_HEAD_SX, selectableRowProps,
} from "../components/FieldConsole";
import { useFormat } from "../farm/useFormat";
import { statusLabel } from "../i18n/enums";

const NOWRAP = { whiteSpace: "nowrap" as const };

// #908 — the Products screen's Packed units tab: the table and its inspector.
export function PackedUnitsPanel({ hidden, conversions, isAdmin, selectedId, onSelect, renderActions }: {
  hidden: boolean;
  conversions: EggUnitConversion[];
  isAdmin: boolean;
  selectedId: string | null;
  onSelect: (id: string | null) => void;
  renderActions: (c: EggUnitConversion) => ReactNode;
}) {
  const { t } = useTranslation("products");
  const { t: tc } = useTranslation("common");
  const fmt = useFormat();
  const selectedConv = conversions.find((c) => c.id === selectedId) ?? null;
  return (
    <Box role="tabpanel" id="packed-units-tabpanel" aria-labelledby="packed-units-tab" hidden={hidden}>
      <ListInspectorPane
        tableLabel={t("packedUnitsHeading")}
        onClearSelection={() => onSelect(null)}
        table={(
          <LedgerTableContainer scrollHint="columnsAndRows">
            <Table size="small" sx={isAdmin ? { "& :is(th, td):last-child": { display: { xs: "none", md: "table-cell" } } } : undefined}>
              <TableHead>
                <TableRow>
                  <TableCell sx={STICKY_TABLE_HEAD_SX}>{t("unitHeader")}</TableCell>
                  <TableCell align="right" sx={STICKY_TABLE_HEAD_SX}>{t("eggsPerUnitHeader")}</TableCell>
                  <TableCell sx={STICKY_TABLE_HEAD_SX}>{t("statusHeader")}</TableCell>
                  {isAdmin && <TableCell sx={STICKY_TABLE_HEAD_SX}>{tc("actions")}</TableCell>}
                </TableRow>
              </TableHead>
              <TableBody>
                {conversions.map((c) => (
                  <TableRow key={c.id} className={c.active ? undefined : "muted"}
                    {...selectableRowProps(c.id === selectedId, () => onSelect(c.id))}>
                    <TableCell sx={NOWRAP}>{c.unitCode}</TableCell>
                    <TableCell align="right" sx={NOWRAP}>{fmt.count(c.eggsPerUnit)}</TableCell>
                    <TableCell sx={NOWRAP}>{statusLabel(c.active ? "Active" : "Inactive")}</TableCell>
                    {isAdmin && <TableCell sx={NOWRAP}>{renderActions(c)}</TableCell>}
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </LedgerTableContainer>
        )}
        inspector={(
          <RecordInspector
            ariaLabel={tc("inspectorLabel", { entity: t("packedUnitEntitySingular") })}
            title={selectedConv?.unitCode}
            emptyMessage={tc("inspectorEmptyPrompt")}
            fields={selectedConv ? [
              { label: t("eggsPerUnitHeader"), value: fmt.count(selectedConv.eggsPerUnit) },
              { label: t("statusHeader"), value: statusLabel(selectedConv.active ? "Active" : "Inactive") },
            ] : undefined}
            actions={isAdmin && selectedConv ? { primary: renderActions(selectedConv) } : undefined}
          />
        )}
      />
    </Box>
  );
}
