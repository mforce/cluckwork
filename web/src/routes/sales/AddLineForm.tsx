import { useState } from "react";
import { Stack, TextField } from "@mui/material";
import { useTranslation } from "react-i18next";
import { parseMoneyToMinorUnits } from "../../api/cluckwork";
import type { Product, SalesOrder } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { CONSOLE_FORM_SX } from "../../components/FieldConsole";
import { NumberField } from "../../components/NumberField";
import { useFormat } from "../../farm/useFormat";
import { priceInput, SELLING_UNITS } from "./orderMath";
import { money, useDiscountPercent, type SalesAction } from "./salesUi";

export function useAddLineFields() {
  const [productId, setProductId] = useState("");
  const [unit, setUnit] = useState("Egg");
  const [qty, setQty] = useState(30);
  const [price, setPrice] = useState("");
  return { productId, setProductId, unit, setUnit, qty, setQty, price, setPrice };
}

export type AddLineFields = ReturnType<typeof useAddLineFields>;

export function AddLineForm({
  active, fields, products, priceScale, eggsPerUnit, listPriceFor, unitWord, addQtyId, action, onAddItem,
}: {
  active: SalesOrder;
  fields: AddLineFields;
  products: Product[];
  priceScale: number | null;
  eggsPerUnit: (sellingUnit: string) => number | null;
  listPriceFor: (product: Product, lineUnit: string) => number | null;
  unitWord: (sellingUnit: string) => string;
  addQtyId: string;
  action: SalesAction;
  onAddItem: () => Promise<unknown>;
}) {
  const { t } = useTranslation("sales");
  const fmt = useFormat();
  const discountPercent = useDiscountPercent();
  const { busy, isPending } = action;
  const { productId, setProductId, unit, setUnit, qty, setQty, price, setPrice } = fields;
  return (
    <>
      <Stack sx={{ ...CONSOLE_FORM_SX, my: 2,
        gridTemplateColumns: { xs: "minmax(0, 1fr)", md: "minmax(0, 1.2fr) minmax(0, .7fr) minmax(145px, 1fr) minmax(0, 1fr)" },
        gap: 1.5,
        "& .numfield input": { minWidth: 0, width: "100%" },
      }}>
        <TextField
          select
          label={t("product")}
          value={productId}
          size="small"
          slotProps={{ select: { native: true } }}
          onChange={(e) => {
            setProductId(e.target.value);
            const p = products.find((x) => x.id === e.target.value);
            if (p) {
              setUnit(p.defaultUnit);
              setPrice(priceInput(p.defaultPriceMinorUnits, priceScale));
            }
          }}
        >
          {products.map((p) => {
            // Suppress the unit only for Egg; a configured one-egg dozen still needs its label.
            const f = p.defaultUnit === "Egg" ? null : eggsPerUnit(p.defaultUnit);
            return (
              <option key={p.id} value={p.id}>
                {f !== null
                  ? t("productOptionWithUnit", { name: p.name, count: f, unit: unitWord(p.defaultUnit) })
                  : p.name}
              </option>
            );
          })}
        </TextField>
        <TextField
          select
          label={t("perLabel")}
          value={unit}
          size="small"
          slotProps={{ select: { native: true } }}
          onChange={(e) => {
            setUnit(e.target.value);
            const p = products.find((x) => x.id === productId);
            if (p) setPrice(priceInput(listPriceFor(p, e.target.value), priceScale));
          }}
        >
          {SELLING_UNITS.map((u) =>
            <option key={u} value={u}>{t(`unit${u}`)}</option>)}
        </TextField>
        {/* Keep the label beside the stepper: it cannot wrap the two buttons. */}
        <div className="numfield-field">
          <label htmlFor={addQtyId}>{t("quantityWithUnit", { unit: unitWord(unit) })}</label>
          <NumberField id={addQtyId} label={t("quantityWithUnit", { unit: unitWord(unit) }).toLowerCase()}
            value={qty} onChange={setQty} min={1} />
          {(() => {

            const f = unit === "Egg" ? null : eggsPerUnit(unit);
            return f !== null
              ? <p className="muted">{t("equalsEggs", { count: qty * f })}</p>
              : null;
          })()}
        </div>
        <TextField
          type="number"
          label={t("unitPriceWithCurrency", { code: active.currencyCode })}
          value={price}
          size="small"
          slotProps={{ htmlInput: { min: 0, step: 10 ** -active.currencyMinorUnit } }}
          onChange={(e) => setPrice(e.target.value)}
        />
        <BusyButton variant="contained" sx={{ gridColumn: "1 / -1", justifySelf: "start", "&&": { width: { xs: "100%", md: "auto" } } }} disabled={busy || !productId} busy={isPending("add-item")}
          onClick={onAddItem}>{t("addLine")}</BusyButton>
      </Stack>
      {/* Keep the hint outside the grid so translations cannot overlap the input. */}
      {(() => {
        // Match the list-price snapshot AddOrderItemHandler would save.
        const shown = products.find((p) => p.id === productId);
        const list = shown ? listPriceFor(shown, unit) : null;
        if (list === null) return null;
        const typed = parseMoneyToMinorUnits(price, active.currencyMinorUnit);
        if (!Number.isFinite(typed) || typed === list) return null;
        const perUnit = Math.abs(typed - list);
        const amount = money(fmt, perUnit, active);
        // A zero list price is legal, and min does not prevent negative input.
        // Percentages require a nonzero list-price denominator.
        if (typed < list) {
          return list === 0
            ? <p className="discount">{t("listPriceHintBelowNoPct", { amount })}</p>
            : <p className="discount">
                {t("listPriceHintBelow", { amount, percent: discountPercent((perUnit * 100) / list) })}
              </p>;
        }
        return list === 0
          ? <p className="discount">{t("listPriceHintAboveNoPct", { amount })}</p>
          : <p className="discount">
              {t("listPriceHintAbove", { amount, percent: discountPercent((perUnit * 100) / list) })}
            </p>;
      })()}
    </>
  );
}
