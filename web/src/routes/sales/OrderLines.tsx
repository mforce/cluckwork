import { Box, Button, Table, TableBody, TableCell, TableHead, TableRow } from "@mui/material";
import { useTranslation } from "react-i18next";
import { parseMoneyToMinorUnits } from "../../api/cluckwork";
import type { SalesOrder } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { NumberField } from "../../components/NumberField";
import { useFormat } from "../../farm/useFormat";
import { listPriceBasisLabel } from "../../i18n/enums";
import { lineExceedsCeiling, type DiscountCeiling } from "../../lib/discountCeiling";
import { lineDiscount, lineDraft } from "./orderMath";
import { LINK_ACTION_SX, MANIFEST_ACTIONS_SX, SECONDARY_ACTION_SX, money, useDiscountPercent, type SalesAction } from "./salesUi";
import type { ActiveOrderState } from "./useActiveOrder";

export function OrderLines({
  active, activeOrder, ceiling, action, editQtyId, productName, onUpdateItem, onRemoveItem,
}: {
  active: SalesOrder;
  activeOrder: ActiveOrderState;
  ceiling: DiscountCeiling | null;
  action: SalesAction;
  editQtyId: string;
  productName: (id: string) => string;
  onUpdateItem: (itemId: string) => Promise<unknown>;
  onRemoveItem: (itemId: string) => Promise<unknown>;
}) {
  const { t } = useTranslation("sales");
  const fmt = useFormat();
  const discountPercent = useDiscountPercent();
  const { busy, isPending } = action;
  const { editor, editorRef, setEditor, editingLine, editConflict, reloadEditor } = activeOrder;
  return (
    <Box sx={{ borderTop: "2px solid var(--ink)", "& .discount": { fontWeight: 750 } }}>
      <Table size="small" sx={{
        "& .MuiTableCell-root": { px: .75, py: .75 },
        display: { xs: "block", md: "table" },
        "& thead, & tbody": { display: { xs: "block", md: "table-row-group" } },
        "& tr": { display: { xs: "grid", md: "table-row" }, gridTemplateColumns: "minmax(0, 1fr) 32px 38px 112px", height: { xs: "auto", md: 36 }, borderBottom: { xs: "1px solid var(--rule)" } },
        "& th, & td": { minWidth: 0, borderBottom: { xs: 0, md: "1px solid var(--rule)" }, overflowWrap: { xs: "anywhere", md: "normal" } },
        "& th": { whiteSpace: { xs: "nowrap", md: editor ? "normal" : "nowrap" } },
        "& td:nth-child(2)": { gridColumn: 2 },
        "& td:nth-child(3)": { gridColumn: 3 },
        "& :is(th, td):nth-child(5), & :is(th, td):nth-child(7), & th:nth-child(4), & th:nth-child(6)": { display: { xs: "none", md: "table-cell" } },
        "& td:nth-child(4)": { gridColumn: "1 / 3", gridRow: 2, textAlign: { xs: "left", md: "right" } },
        "& td:nth-child(6)": { gridColumn: "3 / 5", gridRow: 2 },
        "& :is(th, td):last-child": { gridColumn: 4, gridRow: 1 },
        "& tr[data-editing=true] td:nth-child(2)": {
          gridColumn: "1 / 4", gridRow: 5,
          "& .numfield": { maxWidth: 180 },
          "& .numfield input": { width: { md: "3.875rem" } },
        },
        "& tr[data-editing=true] td:nth-child(5)": { display: { xs: "block", md: "table-cell" }, gridColumn: "1 / 4", "& input": { width: { xs: "100%", md: "4.75rem" }, px: { md: .5 } } },
        "& tr[data-editing=true] td:last-child": { whiteSpace: "normal" },
      }}>
        <TableHead>
          <TableRow>
            <TableCell>{t("product")}</TableCell>
            <TableCell align="right">{t("qty")}</TableCell>
            <TableCell align="right">{t("eggs")}</TableCell>
            <TableCell align="right">{t("listPrice")}</TableCell>
            <TableCell align="right">{t("unitPrice")}</TableCell>
            <TableCell align="right">{t("discount")}</TableCell>
            <TableCell align="right">{t("lineTotal")}</TableCell>
            <TableCell></TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
        {active.items.map((i) => {
          const editingThis = !!editor && editingLine?.id === i.id;
          const typed = editingThis
            ? parseMoneyToMinorUnits(editor.price, active.currencyMinorUnit)
            : Number.NaN;
          const shown = editingThis && Number.isFinite(typed)
            ? { ...i, unitPriceMinorUnits: typed, quantity: editor.quantity }
            : i;
          const discount = lineDiscount(shown);
          const overMaximum = ceiling !== null
            && lineExceedsCeiling(shown.listUnitPriceMinorUnits, shown.unitPriceMinorUnits, ceiling);

          const discountCell = discount.kind === "below"
            ? <span className="discount">
                {`${money(fmt, discount.amountMinorUnits, i)} · ${discountPercent(discount.percent)}%`}
              </span>
            : discount.kind === "above" ? t("aboveList")
              : discount.kind === "none" ? listPriceBasisLabel(i.listPriceBasis)
                : "—";
          return (
          <TableRow key={i.id} data-editing={editingThis}>
            <TableCell>{productName(i.productId)}{" "}
              <span className="muted">{t("perUnit", { unit: i.unit.toLowerCase() })}
                {i.baseUnitFactor > 1 ? ` ${t("eggsCount", { count: i.baseUnitFactor })}` : ""}</span>
              {discount.kind === "below" && (
                <> <Box component="span" className="discount">{t("belowListBadge")}</Box></>
              )}
              {/* Below-list describes the price; over-ceiling warns that confirmation may fail. */}
              {overMaximum && (
                <> <span className="badge badge-danger">{t("overMaximumBadge")}</span></>
              )}
              {discount.kind === "none" && (
                <> <span className="badge">{listPriceBasisLabel(i.listPriceBasis)}</span></>
              )}
              {/* Concept B hides these columns on phones; keep their labelled values accessible (#831). */}
              {!editingThis && <Box component="span" className="sr-only" sx={{ display: { xs: "inline", md: "none" } }}>
                {t("unitPrice")} {money(fmt, i.unitPriceMinorUnits, i)}{", "}
                {t("lineTotal")} {money(fmt, i.unitPriceMinorUnits * i.quantity, i)}
              </Box>}
            </TableCell>
            {editor && editingLine?.id === i.id ? (
              <>
                <TableCell align="right">
                  <label className="sr-only" htmlFor={editQtyId}>{t("editQuantityAriaLabel")}</label>
                  <NumberField id={editQtyId} label={t("editQuantityAriaLabel").toLowerCase()}
                    value={editor.quantity} onChange={(quantity) => {
                      const draft = editorRef.current;
                      if (draft) setEditor({ ...draft, quantity: typeof quantity === "function" ? quantity(draft.quantity) : quantity });
                    }} min={1} />
                </TableCell>
                <TableCell align="right" sx={{ color: "var(--muted)" }}>{fmt.count(i.baseUnitFactor * editor.quantity)}</TableCell>
                <TableCell align="right" sx={{ color: "var(--muted)" }}>
                  <Box component="span" sx={{ display: { xs: "inline", md: "none" } }}>{t("listPrice")}: </Box>{i.listUnitPriceMinorUnits === null
                    ? "—"
                    : money(fmt, i.listUnitPriceMinorUnits, i)}
                </TableCell>
                <TableCell align="right"><Box component="span" sx={{ display: { xs: "block", md: "none" } }}>{t("unitPrice")}</Box><input className="cell" type="number" min={0}
                  aria-label={t("editUnitPriceAriaLabel")}
                  step={10 ** -active.currencyMinorUnit} value={editor.price}
                  onChange={(e) => {
                    const draft = editorRef.current;
                    if (draft) setEditor({ ...draft, price: e.target.value });
                  }} /></TableCell>
                <TableCell align="right"><Box component="span" sx={{ display: { xs: "inline", md: "none" } }}>{t("discount")}: </Box>{discountCell}</TableCell>
                <TableCell align="right">—</TableCell>
                <TableCell sx={MANIFEST_ACTIONS_SX}>
                  <Box sx={{ display: "inline-flex", flexWrap: "nowrap", alignItems: "center" }}>
                    <BusyButton variant="text" size="small" sx={LINK_ACTION_SX} disabled={busy || editConflict} busy={isPending(`update-item:${i.id}`)}
                      onClick={() => onUpdateItem(i.id)}>{t("save")}</BusyButton>
                    <Button size="small" sx={SECONDARY_ACTION_SX} onClick={() => setEditor(null)}>{t("cancelEdit")}</Button>
                  </Box>
                  {editConflict && (
                    <div role="status">
                      {t("editConflict")} {" "}
                      <button className="link" onClick={reloadEditor}>{t("reloadLine")}</button>
                    </div>
                  )}
                </TableCell>
              </>
            ) : (
              <>
                <TableCell align="right">{fmt.count(i.quantity)}</TableCell>
                <TableCell align="right">{fmt.count(i.quantityBase)}</TableCell>
                <TableCell align="right">
                  <Box component="span" sx={{ display: { xs: "inline", md: "none" } }}>{t("listPrice")}: </Box>{i.listUnitPriceMinorUnits === null
                    ? "—"
                    : discount.kind === "below"
                      ? <s>{money(fmt, i.listUnitPriceMinorUnits, i)}</s>
                      : money(fmt, i.listUnitPriceMinorUnits, i)}
                </TableCell>
                <TableCell align="right">{money(fmt, i.unitPriceMinorUnits, i)}</TableCell>
                <TableCell align="right"><Box component="span" sx={{ display: { xs: "inline", md: "none" } }}>{t("discount")}: </Box>{discountCell}</TableCell>
                <TableCell align="right">{money(fmt, i.unitPriceMinorUnits * i.quantity, i)}</TableCell>
                <TableCell sx={MANIFEST_ACTIONS_SX}>
                  {active.status === "Draft" && (
                    <>
                      <Button variant="text" size="small" sx={LINK_ACTION_SX} disabled={busy} onClick={() => {
                        setEditor(lineDraft(active, i));
                      }}>{t("edit")}</Button>
                      <BusyButton variant="text" size="small" sx={{ ...LINK_ACTION_SX, color: "var(--error)" }} disabled={busy} busy={isPending(`remove-item:${i.id}`)}
                        onClick={() => onRemoveItem(i.id)}>{t("remove")}</BusyButton>
                    </>
                  )}
                </TableCell>
              </>
            )}
          </TableRow>
          );
        })}
        </TableBody>
      </Table>
    </Box>
  );
}
