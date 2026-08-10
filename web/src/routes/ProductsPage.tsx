import { useEffect, useId, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Plus } from "lucide-react";
import {
  activateProduct, createProduct, deactivateProduct, formatMoney,
  getAccount, listEggGrades, listEggUnitConversions, listProducts,
  updateEggUnitConversion, updateProduct,
} from "../api/cluckwork";
import type { EggGrade, EggUnitConversion, Product } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { DialogError } from "../components/DialogError";
import { NumberField } from "../components/NumberField";
import { StatusBadge } from "../components/StatusBadge";
import { useDialogErrors } from "../components/useDialogErrors";
import { usePendingAction } from "../components/usePendingAction";
import { newId } from "../lib/ids";
import i18n from "../i18n";
import { statusLabel } from "../i18n/enums";

// Spec §10.1 default_unit values usable for egg products; packed units resolve
// through the conversions below at sale time (part 2 of #97).
// "Other" is deliberately absent: it has no packed-unit conversion row this
// phase, so an Other product could never resolve to eggs (codex review of #98).
const EGG_UNITS = ["Egg", "Dozen", "Flat", "Tray", "Carton", "Case"];

function errorMessage(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// #97 (part 1): the product catalog — what the farm sells. Egg products map to
// an egg grade; sales screens switch from raw grades to products in part 2.
// No hard delete — future sold lines reference products forever.
export function ProductsPage() {
  const { t } = useTranslation("products");
  const { t: tc } = useTranslation("common");
  const { isAdmin } = useAuth();
  const [products, setProducts] = useState<Product[] | null>(null);
  const [grades, setGrades] = useState<EggGrade[]>([]);
  const [conversions, setConversions] = useState<EggUnitConversion[]>([]);
  // #479 — one slot per PLACE a message can appear: the page (mount load) and
  // each dialog by its own scope.
  const errors = useDialogErrors();
  const setPageError = errors.setPage;
  // #236 — the shared flight guard. `busy` inerts the whole screen; the one
  // clicked trigger additionally spins via isPending(scope).
  const { busy, isPending, run: runPending } = usePendingAction();

  // create form (F131: in a dialog)
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState("");
  const [unit, setUnit] = useState("Dozen");
  const [gradeId, setGradeId] = useState("");
  const [price, setPrice] = useState("");
  const [notes, setNotes] = useState("");

  // edit (products) — dialog seeded from the row
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState("");
  const [editUnit, setEditUnit] = useState("Dozen");
  const [editGradeId, setEditGradeId] = useState("");
  const [editPrice, setEditPrice] = useState("");
  const [editNotes, setEditNotes] = useState("");

  // edit (conversions) — dialog seeded from the row
  const [editingConvId, setEditingConvId] = useState<string | null>(null);
  const [editEggs, setEditEggs] = useState(1);
  // NumberField owns its own input, so the label points at it by id (#250).
  const eggsFieldId = useId();
  const [editConvActive, setEditConvActive] = useState(true);

  // CREATE prices parse with the ACCOUNT currency (what the new product will
  // snapshot); EDIT prices parse with that product's own snapshot — never
  // another row's precision (codex review of #98).
  const [currency, setCurrency] = useState<{ code: string; minor: number }>({ code: "", minor: 2 });

  const keys = useRef(new Map<string, string>());
  const keyFor = (scope: string) => {
    const existing = keys.current.get(scope);
    if (existing) return existing;
    const fresh = newId();
    keys.current.set(scope, fresh);
    return fresh;
  };
  const clearKey = (scope: string) => keys.current.delete(scope);

  const refresh = async () => {
    const [p, c] = await Promise.all([
      listProducts({ includeInactive: true }),
      listEggUnitConversions(),
    ]);
    setProducts(p);
    setConversions(c);
  };

  useEffect(() => {
    Promise.all([
      listProducts({ includeInactive: true }),
      listEggUnitConversions(),
      listEggGrades(),
      getAccount(),
    ])
      .then(([p, c, g, a]) => {
        setProducts(p);
        setConversions(c);
        setGrades(g.filter((x) => x.isSaleable));
        setCurrency({ code: a.currencyCode, minor: a.currencyMinorUnit });
      })
      .catch(() => setPageError(i18n.t("products:loadCatalogFailed")));
  }, [setPageError]);

  // Exact string parsing — never float × 10^n (money rule).
  const toMinorUnits = (display: string, minor: number): number | null => {
    const trimmed = display.trim();
    if (!trimmed) return null;
    const match = /^(\d+)(?:\.(\d+))?$/.exec(trimmed);
    if (!match) throw new Error(i18n.t("products:enterPriceAsNumber"));
    const frac = match[2] ?? "";
    if (frac.length > minor)
      throw new Error(minor === 0
        ? i18n.t("products:noDecimalPlaces")
        : i18n.t("products:atMostDecimals", { count: minor }));
    return Number(match[1]) * 10 ** minor + Number(frac.padEnd(minor, "0") || 0);
  };

  // Rebased on usePendingAction (#236): the hook owns the re-entry guard and
  // the pending scope; the idempotency-key/refresh-before-rotate body stays
  // exactly as reviewed. The same scope string doubles as the key scope.
  // `errorScope` names the DIALOG a failure belongs to; `null` routes it to
  // the page — deactivate/activate run from row buttons, not a dialog.
  async function run(scope: string, errorScope: string | null, action: (key: string) => Promise<unknown>) {
    const ok = await runPending(scope, async () => {
      errors.beginAttempt(errorScope);
      try {
        await action(keyFor(scope));
        // Refresh must succeed before the key rotates (idempotent retry contract).
        await refresh();
        clearKey(scope);
        return true;
      } catch (err) {
        errors.report(errorScope, errorMessage(err));
        return false;
      }
    });
    // A SKIPPED run (undefined — another flight was open) is not a success: it
    // must never close a dialog or reset a form as if it were.
    return ok ?? false;
  }

  // Dismissal empties this dialog's slot and mutes the attempt still out, so a
  // late failure is not reported against a session the user reopened.
  const closeCreate = () => { setCreating(false); errors.abandon("create"); };
  const closeEdit = () => { setEditingId(null); errors.abandon("edit"); };
  const closeEditConversion = () => { setEditingConvId(null); errors.abandon("edit-conversion"); };

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    // The attempt starts here, not inside `run` — a validation throw below
    // returns before `run` (and its own beginAttempt) is ever reached, and
    // without this the slot would still carry a MUTE from a prior dismissal,
    // silently swallowing this attempt's own validation message.
    errors.beginAttempt("create");
    let priceMinor: number | null;
    try {
      priceMinor = toMinorUnits(price, currency.minor);
    } catch (err) {
      errors.report("create", errorMessage(err));
      return;
    }
    const ok = await run("create-product", "create", (key) =>
      createProduct({
        name,
        productType: "Egg",
        defaultUnit: unit,
        defaultPriceMinorUnits: priceMinor,
        eggGradeId: gradeId,
        notes: notes.trim() || null,
      }, key));
    if (ok) {
      setName("");
      setPrice("");
      setNotes("");
      setCreating(false);
    }
  }

  // Opening a dialog over a still-open one is a DISPLACEMENT: the first
  // session ends without `onClose` ever running, so its `abandon` never fires
  // and whatever verdict it left is still in the slot the next session
  // renders. The backdrop keeps a mouse off the row buttons underneath, but
  // #480 established it does not stop a screen reader's virtual cursor — the
  // same door the per-dialog map exists for. A displacing open therefore
  // abandons what it displaces, INCLUDING its own scope when that scope is
  // fixed across records (pi review of #491). Never on the same record: a 409
  // rebind reopens the identical scope and then reports into it.
  function startEdit(p: Product) {
    closeCreate();
    closeEditConversion();
    if (editingId !== null && editingId !== p.id) errors.abandon("edit");
    setEditingId(p.id);
    setEditName(p.name);
    setEditUnit(p.defaultUnit);
    setEditGradeId(p.eggGradeId ?? "");
    setEditPrice(p.defaultPriceMinorUnits === null
      ? ""
      : (p.defaultPriceMinorUnits / 10 ** p.currencyMinorUnit).toFixed(p.currencyMinorUnit));
    setEditNotes(p.notes ?? "");
  }

  // Same displacement rule as startEdit, for the conversion dialog's own
  // fixed scope.
  function startEditConversion(c: EggUnitConversion) {
    closeCreate();
    closeEdit();
    if (editingConvId !== null && editingConvId !== c.id) errors.abandon("edit-conversion");
    setEditingConvId(c.id);
    setEditEggs(c.eggsPerUnit);
    setEditConvActive(c.active);
  }

  async function onSaveEdit(e: FormEvent) {
    e.preventDefault();
    const id = editingId;
    if (id === null) return;
    // See onCreate: the attempt starts here so a validation throw below still
    // un-mutes and clears this dialog's own slot.
    errors.beginAttempt("edit");
    const target = products?.find((p) => p.id === id);
    let priceMinor: number | null;
    try {
      priceMinor = toMinorUnits(editPrice, target?.currencyMinorUnit ?? currency.minor);
    } catch (err) {
      errors.report("edit", errorMessage(err));
      return;
    }
    const ok = await run(`update:${id}`, "edit", (key) =>
      updateProduct(id, {
        name: editName,
        defaultUnit: editUnit,
        defaultPriceMinorUnits: priceMinor,
        eggGradeId: editGradeId,
        notes: editNotes.trim() || null,
      }, key));
    if (ok) setEditingId(null);
  }

  async function onSaveConversion(e: FormEvent) {
    e.preventDefault();
    const id = editingConvId;
    if (id === null) return;
    const ok = await run(`conv:${id}`, "edit-conversion", (key) =>
      updateEggUnitConversion(id, { eggsPerUnit: editEggs, active: editConvActive }, key));
    if (ok) setEditingConvId(null);
  }

  const gradeName = (id: string | null) =>
    grades.find((g) => g.id === id)?.name ?? (id ? id.slice(0, 8) : "—");

  if (errors.page && products === null) {
    return <section><h2>{t("title")}</h2><p className="error">{errors.page}</p></section>;
  }
  if (products === null) {
    return <section><h2>{t("title")}</h2><p className="muted">{tc("loading")}</p></section>;
  }

  const editingProduct = products.find((p) => p.id === editingId) ?? null;
  const editingConv = conversions.find((c) => c.id === editingConvId) ?? null;

  return (
    <section>
      <div className="page-head">
        <h2>{t("title")}</h2>
        {isAdmin && (
          <button type="button" onClick={() => { closeEdit(); closeEditConversion(); setCreating(true); }}>
            <Plus size={16} aria-hidden /> {t("newProductButton")}
          </button>
        )}
      </div>
      <p className="muted">
        {t("intro")}
      </p>

      {/* #479 — unconditional: each dialog now renders its own failure through
          its own slot (DialogError below), so nothing here can be a stale copy
          of a dialog's message; this is only ever the page's own. */}
      {errors.page && <p className="error" role="alert">{errors.page}</p>}

      {/* Gated like the inline form was: a role change mid-edit closes it. */}
      <Dialog open={creating && isAdmin} title={t("newProductDialogTitle")} onClose={closeCreate}>
        <form onSubmit={(e) => void onCreate(e)} className="inline-form">
          <label>{t("nameLabel")}
            <input value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} />
          </label>
          <label>{t("gradeLabel")}
            <select value={gradeId} onChange={(e) => setGradeId(e.target.value)} required>
              <option value="">{t("pickGradeOption")}</option>
              {grades.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
            </select>
          </label>
          <label>{t("soldPerLabel")}
            <select value={unit} onChange={(e) => setUnit(e.target.value)}>
              {EGG_UNITS.map((u) => <option key={u} value={u}>{u}</option>)}
            </select>
          </label>
          <label>{currency.code ? t("defaultPriceWithCurrencyLabel", { code: currency.code }) : t("defaultPriceLabel")}
            <input type="number" min="0" step={(1 / 10 ** currency.minor).toFixed(currency.minor)}
              value={price} onChange={(e) => setPrice(e.target.value)} placeholder={t("priceOptionalPlaceholder")} />
          </label>
          <label>{t("notesLabel")}
            <input value={notes} onChange={(e) => setNotes(e.target.value)} maxLength={500} />
          </label>
          <DialogError errors={errors} scope="create" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeCreate}>{tc("cancel")}</button>
            <BusyButton disabled={busy} busy={isPending("create-product")}>{t("addProductButton")}</BusyButton>
          </div>
        </form>
      </Dialog>

      <Dialog open={editingProduct !== null && isAdmin} title={t("editProductDialogTitle")} onClose={closeEdit}>
        {/* noValidate: the row's save used to be a plain button, so the browser
            never enforced min/step — the price parser's own message
            ("At most N decimal places for this currency") did. */}
        <form onSubmit={(e) => void onSaveEdit(e)} className="inline-form" noValidate>
          <label>{t("nameLabel")}
            <input value={editName} onChange={(e) => setEditName(e.target.value)} maxLength={100} />
          </label>
          <label>{t("gradeLabel")}
            <select value={editGradeId} onChange={(e) => setEditGradeId(e.target.value)}>
              {grades.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
            </select>
          </label>
          <label>{t("soldPerLabel")}
            <select value={editUnit} onChange={(e) => setEditUnit(e.target.value)}>
              {EGG_UNITS.map((u) => <option key={u} value={u}>{u}</option>)}
            </select>
          </label>
          {/* Stepped by THIS product's snapshot precision, not the account's. */}
          <label>{editingProduct ? t("defaultPriceWithCurrencyLabel", { code: editingProduct.currencyCode }) : t("defaultPriceLabel")}
            <input type="number" min="0"
              step={editingProduct ? (1 / 10 ** editingProduct.currencyMinorUnit).toFixed(editingProduct.currencyMinorUnit) : "0.01"}
              value={editPrice} onChange={(e) => setEditPrice(e.target.value)} />
          </label>
          {/* No notes field: the inline edit had none, and #131 changes shape,
              not capability. editNotes stays seeded so the body round-trips. */}
          <DialogError errors={errors} scope="edit" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeEdit}>{tc("cancel")}</button>
            <BusyButton type="submit" disabled={busy}
              busy={editingId !== null && isPending(`update:${editingId}`)}>{tc("save")}</BusyButton>
          </div>
        </form>
      </Dialog>

      <Dialog
        open={editingConv !== null && isAdmin}
        title={editingConv ? t("eggsPerUnit", { unitCode: editingConv.unitCode }) : t("packedUnitDialogTitle")}
        onClose={closeEditConversion}
      >
        <form onSubmit={(e) => void onSaveConversion(e)} className="inline-form" noValidate>
          {/* #250: sibling label, not wrapping — a <label> may not contain
              interactive content other than its own control, and the stepper
              carries two buttons. */}
          <div className="numfield-field">
            <label htmlFor={eggsFieldId}>{t("eggsPerUnitFieldLabel")}</label>
            <NumberField id={eggsFieldId} label={t("eggsPerUnitFieldLabel").toLowerCase()}
              value={editEggs} onChange={setEditEggs} min={1} />
          </div>
          <label className="check">
            <input type="checkbox" checked={editConvActive}
              onChange={(e) => setEditConvActive(e.target.checked)} /> {t("activeCheckboxLabel")}
          </label>
          <DialogError errors={errors} scope="edit-conversion" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeEditConversion}>{tc("cancel")}</button>
            <BusyButton type="submit" disabled={busy}
              busy={editingConvId !== null && isPending(`conv:${editingConvId}`)}>{tc("save")}</BusyButton>
          </div>
        </form>
      </Dialog>

      {products.length === 0 ? (
        <p className="muted">{t("noProductsMessage")}</p>
      ) : (
        <table className="data">
          <thead>
            <tr><th>{t("nameHeader")}</th><th>{t("gradeHeader")}</th><th>{t("soldPerHeader")}</th><th>{t("defaultPriceHeader")}</th><th>{t("statusHeader")}</th>{isAdmin && <th>{tc("actions")}</th>}</tr>
          </thead>
          <tbody>
            {products.map((p) => (
              <tr key={p.id} className={p.active ? undefined : "muted"}>
                <td title={p.notes ?? undefined}>{p.name}</td>
                <td>{gradeName(p.eggGradeId)}</td>
                <td>{p.defaultUnit}</td>
                <td>{p.defaultPriceMinorUnits === null
                  ? "—"
                  : formatMoney(p.defaultPriceMinorUnits, p.currencyCode, p.currencyMinorUnit)}</td>
                <td><StatusBadge status={p.active ? "Active" : "Inactive"} label={statusLabel(p.active ? "Active" : "Inactive")} /></td>
                {isAdmin && (
                  <td>
                    <button className="link" disabled={busy} onClick={() => startEdit(p)}>{t("editButton")}</button>{" "}
                    {p.active ? (
                      <BusyButton className="link" disabled={busy} busy={isPending(`deact:${p.id}`)}
                        onClick={() => void run(`deact:${p.id}`, null, (key) => deactivateProduct(p.id, key))}>
                        {t("deactivateButton")}
                      </BusyButton>
                    ) : (
                      <BusyButton className="link" disabled={busy} busy={isPending(`act:${p.id}`)}
                        onClick={() => void run(`act:${p.id}`, null, (key) => activateProduct(p.id, key))}>
                        {t("activateButton")}
                      </BusyButton>
                    )}
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <h3>{t("packedUnitsHeading")}</h3>
      <p className="muted">
        {t("packedUnitsIntro")}
      </p>
      <table className="data">
        <thead>
          <tr><th>{t("unitHeader")}</th><th>{t("eggsPerUnitHeader")}</th><th>{t("statusHeader")}</th>{isAdmin && <th>{tc("actions")}</th>}</tr>
        </thead>
        <tbody>
          {conversions.map((c) => (
            <tr key={c.id} className={c.active ? undefined : "muted"}>
              <td>{c.unitCode}</td>
              <td>{c.eggsPerUnit}</td>
              <td>{statusLabel(c.active ? "Active" : "Inactive")}</td>
              {isAdmin && (
                <td>
                  {c.unitCode === "Individual" ? (
                    <span className="muted">{t("alwaysOneMessage")}</span>
                  ) : (
                    <button className="link" disabled={busy}
                      onClick={() => startEditConversion(c)}>
                      {t("editButton")}
                    </button>
                  )}
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
