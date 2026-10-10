import { useTranslation } from "react-i18next";
import {
  addOrderItem, cancelOrder, confirmOrder, getOrder, listEggGrades, listEggUnitConversions,
  listProducts, parseMoneyToMinorUnits, removeOrderItem, updateOrderItem, voidOrder,
} from "../../api/cluckwork";
import type { EggUnitConversion, OrderItem, Product, SalesOrder } from "../../api/cluckwork";
import { ApiError } from "../../api/client";
import type { useConfirm } from "../../components/useConfirm";
import { useFormat } from "../../farm/useFormat";
import i18n from "../../i18n";
import { DISCOUNT_REASON_VALUES, discountReasonLabel } from "../../i18n/enums";
import type { DiscountReasonValue } from "../../i18n/enums";
import { newId } from "../../lib/ids";
import type { AddLineFields } from "./AddLineForm";
import { editableLine, lineChanged, lineDiscount, orderDiscount, sellableProducts } from "./orderMath";
import { money, useDiscountPercent, type PagedOrders, type SalesAction } from "./salesUi";
import type { ActiveOrderState } from "./useActiveOrder";

interface CommandDeps {
  action: SalesAction;
  orders: PagedOrders;
  keyFor: (scope: string) => string;
  clearKey: (scope: string) => void;
  setMessage: (message: string | null) => void;
  dialogs: Pick<ReturnType<typeof useConfirm>, "confirm" | "askReason" | "askChoice">;
  activeOrder: ActiveOrderState;
  addLine: AddLineFields;
  products: Product[];
  productName: (id: string) => string;
  eggsPerUnit: (sellingUnit: string) => number | null;
  listPriceFor: (product: Product, lineUnit: string) => number | null;
  setConversions: (conversions: EggUnitConversion[]) => void;
  setAllProducts: (products: Product[]) => void;
  setProducts: (products: Product[]) => void;
}

// The order and line writes, and the order panel's read.
export function useOrderCommands({
  action, orders, keyFor, clearKey, setMessage, dialogs, activeOrder, addLine,
  products, productName, eggsPerUnit, listPriceFor, setConversions, setAllProducts, setProducts,
}: CommandDeps) {
  const { t } = useTranslation("sales");
  const fmt = useFormat();
  const discountPercent = useDiscountPercent();
  const { run, startLoad } = action;
  const { confirm, askReason, askChoice } = dialogs;
  const { active, activeRef, activeIdRef, editorRef, setActive, setEditor, itemUpdateAttempts } = activeOrder;
  const { productId, unit, qty, price } = addLine;

  // #1183 — a line change moves the order's total, discount and outstanding,
  // so the Orders list re-reads its window too, under runWrite's ticket (#469).
  const writeLine = (id: string, write: () => Promise<unknown>) => orders.runWrite(async () => {
    await write();
    const refreshed = await getOrder(id);
    if (activeIdRef.current === id) setActive(refreshed);
  });

  const onAddItem = () => run("add-item", async () => {
    if (!active) return;
    const id = active.id;
    // #398 — sales quantities are whole selling units; reject a fractional
    // value BEFORE sending rather than letting the server's JSON binding
    // fail with an internal parameter-binding message. NumberField's typed
    // input isn't step-constrained (no wrapping <form> — see the comment
    // above the new-order dialog in SalesPage.tsx), so `qty` can legitimately hold
    // e.g. 2.5 here.
    if (!Number.isInteger(qty)) throw new Error(i18n.t("sales:quantityMustBeWholeNumber"));
    // Empty price → omit it: the server falls back to the product's default.
    let minorUnits: number | undefined;
    if (price.trim() !== "") {
      minorUnits = parseMoneyToMinorUnits(price, active.currencyMinorUnit);
      if (!Number.isFinite(minorUnits) || minorUnits < 0) throw new Error(i18n.t("sales:invalidUnitPrice"));
    }
    const scope = `add-item:${id}`;
    // #445 — bind the previewed factor to the write: if an admin redefined
    // the unit after this page read its conversions, the server refuses
    // (SalesOrder.UnitDefinitionChanged) instead of recording a QuantityBase
    // different from the "= N eggs" the seller saw. undefined when nothing
    // was previewed (per-egg unit, or no/failed conversions read).
    const previewed = unit === "Egg" ? null : eggsPerUnit(unit);
    try {
      await writeLine(id, () => addOrderItem(id,
        {
          productId, quantity: qty, unit, unitPriceMinorUnits: minorUnits,
          expectedEggsPerUnit: previewed ?? undefined,
          ...(() => {
            // #720 — presence matters: "the seller saw no list price" is an
            // expectation, and it is not the same as having no opinion.
            const shown = products.find((p) => p.id === productId);
            if (!shown) return {};
            if (shown.defaultPriceMinorUnits === null) return { expectedListPriceIsUnset: true };
            const list = listPriceFor(shown, unit);
            return list === null ? {} : { expectedListUnitPriceMinorUnits: list };
          })(),
        },
        keyFor(scope)));
    } catch (err) {
      // Any server rejection may mean the conversions moved under us (the
      // UnitDefinitionChanged case) — refresh them so the preview and the
      // next attempt use the current factors instead of looping on stale
      // ones. Fire-and-forget: the thrown error still surfaces normally.
      // #720 — products too, not just conversions: a ListPriceChanged refusal
      // means the catalogue moved, and without this the retry loops on the
      // same stale price forever.
      if (err instanceof ApiError) {
        listEggUnitConversions().then(setConversions).catch(() => {});
        Promise.all([listProducts({ includeInactive: true }), listEggGrades()])
          .then(([p, g]) => { setAllProducts(p); setProducts(sellableProducts(p, g)); })
          .catch(() => {});
      }
      throw err;
    }
    clearKey(scope);
  });

  const onUpdateItem = (itemId: string) => run(`update-item:${itemId}`, async () => {
    const order = activeRef.current;
    const draft = editorRef.current;
    const item = editableLine(order, draft);
    if (!order || !draft || !item || draft.itemId !== itemId || lineChanged(item, draft)) return;
    const id = order.id;
    // #398 — same whole-number guard as the add-line control, above.
    if (!Number.isInteger(draft.quantity)) throw new Error(i18n.t("sales:quantityMustBeWholeNumber"));
    const minorUnits = parseMoneyToMinorUnits(draft.price, order.currencyMinorUnit);
    if (!Number.isFinite(minorUnits) || minorUnits < 0) throw new Error(i18n.t("sales:invalidUnitPrice"));
    const scope = `update-item:${itemId}`;
    const previous = itemUpdateAttempts.current.get(scope);
    const attempt = previous?.quantity === draft.quantity && previous.unitPriceMinorUnits === minorUnits
      ? previous
      : {
        quantity: draft.quantity, unitPriceMinorUnits: minorUnits, key: newId(),
        serverQuantity: draft.serverQuantity, serverPrice: draft.serverPrice,
      };
    itemUpdateAttempts.current.set(scope, attempt);
    await writeLine(id, async () => {
      await updateOrderItem(id, itemId,
        { quantity: draft.quantity, unitPriceMinorUnits: minorUnits }, attempt.key);
      if (editorRef.current === draft) setEditor(null);
    });
    itemUpdateAttempts.current.delete(scope);
  });

  const onRemoveItem = (itemId: string) => run(`remove-item:${itemId}`, async () => {
    if (!active) return;
    const id = active.id;
    const scope = `remove-item:${itemId}`;
    await writeLine(id, () => removeOrderItem(id, itemId, keyFor(scope)));
    clearKey(scope);
  });

  // #721 — the two figures the confirm dialog shows before it asks for a
  // reason. Both read the SAME orderDiscount/lineDiscount the order panel and
  // the Orders list already render, so the dialog can never quote a different
  // number from the screen behind it.
  const discountHeadline = (order: SalesOrder) => {
    const level = orderDiscount(order.items);
    if (level.kind !== "below") return null;
    const amount = money(fmt, level.amountMinorUnits, order);
    const counts = {
      below: order.items.filter((i) => lineDiscount(i).kind === "below").length,
      total: order.items.length,
    };
    return (
      <p className="discount" data-testid="confirm-discount-headline">
        {level.percent === null
          ? t("discountReasonHeadlineNoPct", { amount, ...counts })
          : t("discountReasonHeadline",
              { amount, percent: discountPercent(level.percent), ...counts })}
      </p>
    );
  };

  const lineDiscountText = (item: OrderItem) => {
    const line = lineDiscount(item);
    if (line.kind !== "below") return null;
    const amount = money(fmt, line.amountMinorUnits, item);
    // A zero list price cannot reach here — lineDiscount's "below" needs a
    // negative unit price, which the validator refuses — but percent is still
    // computed from `list`, so the amount-only variant stays as the honest
    // fallback rather than a division nobody can read.
    return line.percent > 0
      ? t("discountReasonLine", { amount, percent: discountPercent(line.percent) })
      : t("discountReasonLineNoPct", { amount });
  };

  // One-way actions (#59). Confirm BEFORE run() so buttons don't flash
  // disabled while the user decides.
  const onConfirm = async () => {
    if (!active) return;
    // #721 — a below-list order must carry a reason, and the server refuses one
    // on an order that has nothing below list, so the same predicate decides
    // both which dialog opens and whether a body is sent. lineDiscount is that
    // predicate already; a second one here could drift from it.
    const belowLines = active.items.filter((item) => lineDiscount(item).kind === "below");
    let body: { discountReasonCode: string; discountReasonNote?: string } | undefined;
    if (belowLines.length > 0) {
      const picked = await askChoice({
        title: i18n.t("sales:confirmOrderTitle"),
        // The FIFO prose the plain confirmation has always carried, then what
        // this order gives away, then the lines it gives it away on: the person
        // confirming sees the number before they justify it.
        body: (
          <>
            <p>{i18n.t("sales:confirmOrderBody")}</p>
            {discountHeadline(active)}
            <ul className="discount-breakdown" data-testid="confirm-discount-lines">
              {belowLines.map((item) => (
                <li key={item.id}>
                  <span>{productName(item.productId)}</span>
                  <span className="discount">{lineDiscountText(item)}</span>
                </li>
              ))}
            </ul>
          </>
        ),
        confirmLabel: i18n.t("sales:confirmOrderConfirmLabel"),
        choiceLabel: i18n.t("sales:discountReasonLabel"),
        choices: DISCOUNT_REASON_VALUES.map((value) => ({
          value,
          label: discountReasonLabel(value),
        })),
        // `satisfies` so a rename of the server member that reaches
        // DISCOUNT_REASON_VALUES fails typecheck here rather than silently
        // dropping the inline note requirement. Pinned from the C# side too by
        // DiscountReasonVocabularyTests.
        noteRequiredFor: ["Other" satisfies DiscountReasonValue],
        noteLabel: i18n.t("sales:discountReasonNoteLabel"),
        noteRequiredMessage: i18n.t("sales:discountReasonNoteRequired"),
        choiceRequiredMessage: i18n.t("sales:discountReasonRequired"),
      });
      if (picked === null) return;
      body = { discountReasonCode: picked.value };
      if (picked.note !== null) body.discountReasonNote = picked.note;
    } else {
      const ok = await confirm({
        title: i18n.t("sales:confirmOrderTitle"),
        body: i18n.t("sales:confirmOrderBody"),
        confirmLabel: i18n.t("sales:confirmOrderConfirmLabel"),
      });
      if (!ok) return;
    }
    const id = active.id;
    void run(`confirm:${id}`, async () => {
      const scope = `confirm:${id}`;
      await orders.runWrite(async () => {
        await confirmOrder(id, body, keyFor(scope));
        const refreshed = await getOrder(id);
        if (activeIdRef.current === id) {
          setActive(refreshed);
          setMessage(i18n.t("sales:orderConfirmed", { ref: refreshed.referenceNumber }));
        }
      });
      clearKey(scope);
    });
  };

  const onCancel = async () => {
    // Cancel is a status change: the order keeps its lines but becomes
    // read-only and can't be confirmed.
    const ok = await confirm({
      title: i18n.t("sales:cancelDraftTitle"),
      body: i18n.t("sales:cancelDraftBody"),
      confirmLabel: i18n.t("sales:cancelDraft"),
      destructive: true,
    });
    if (!ok || !active) return;
    const id = active.id;
    void run(`cancel:${id}`, async () => {
      const scope = `cancel:${id}`;
      await orders.runWrite(async () => {
        await cancelOrder(id, keyFor(scope));
        if (activeIdRef.current === id) {
          setActive(null);
          setMessage(i18n.t("sales:draftOrderCancelled"));
        }
      });
      clearKey(scope);
    });
  };

  // Undo of a mistaken confirm (#60). Reason prompt doubles as the confirm
  // dialog, hoisted above run() like the other one-way actions; cancelling the
  // prompt aborts the void.
  const onVoid = async () => {
    const reason = await askReason({
      title: i18n.t("sales:voidOrderTitle"),
      body: i18n.t("sales:voidOrderBody"),
      confirmLabel: i18n.t("sales:voidOrderConfirmLabel"),
      destructive: true,
    });
    if (reason === null || !active) return;
    const id = active.id;
    void run(`void:${id}`, async () => {
      const scope = `void:${id}`;
      await orders.runWrite(async () => {
        await voidOrder(id, reason, keyFor(scope));
        const refreshed = await getOrder(id);
        if (activeIdRef.current === id) {
          setActive(refreshed);
          setMessage(i18n.t("sales:orderVoided", { ref: refreshed.referenceNumber }));
        }
      });
      clearKey(scope);
    });
  };

  // Always fetch fresh on open — the list row may be stale relative to
  // mutations made through the panel since the list was loaded.
  const onOpen = (id: string) => run(`open:${id}`, async () => {
    const current = startLoad("order-panel");
    const loaded = await getOrder(id);
    if (current()) setActive(loaded);
  });

  return { onAddItem, onUpdateItem, onRemoveItem, onConfirm, onCancel, onVoid, onOpen };
}
