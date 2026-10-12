import { Fragment, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Package, Plus, TriangleAlert } from "lucide-react";
import { Box, Stack, Tab, Table, TableBody, TableCell, Tabs, TableHead, TableRow, Tooltip } from "@mui/material";
import {
  activateProduct, createProduct, deactivateProduct,
  getAccount, listEggGrades, listEggUnitConversions, listProducts,
  updateEggUnitConversion, updateProduct,
} from "../api/cluckwork";
import type { EggGrade, EggUnitConversion, Product } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useFormat } from "../farm/useFormat";
import { useAuth } from "../auth/useAuth";
import { BusyButton } from "../components/BusyButton";
import {
  CONSOLE_DESTRUCTIVE_LINK_SX, CONSOLE_LINK_SX, LedgerTableContainer, ListInspectorPane, RecordInspector,
  STICKY_TABLE_HEAD_SX, selectableRowProps, useClampSelection,
} from "../components/FieldConsole";
import { EmptyState } from "../components/EmptyState";
import { PackedUnitsPanel } from "./PackedUnitsPanel";
import { ProductDialogs } from "./ProductDialogs";
import { StatusBadge } from "../components/StatusBadge";
import { useDialogAction } from "../components/useDialogAction";
import { newId } from "../lib/ids";
import i18n from "../i18n";
import { statusLabel } from "../i18n/enums";

// The scopes that own a dialog (#703). A scope outside the list — the row
// activate/deactivate writes — reports to the page and is never superseded.
const DIALOG_SCOPES = ["create", "edit", "edit-conversion"] as const;

// MUI's auto table layout shrinks any wrappable cell below its content width,
// so short values (names, numbers, chips, actions) are pinned; free text wraps.
const NOWRAP = { whiteSpace: "nowrap" as const };

// The 409s that mean "the row changed since it was loaded": the handlers' stale
// base Version, and the EF token's race (Program.cs). Every other 409 is a
// different failure and keeps the form and its idempotency key.
const VERSION_CONFLICT_TITLES = new Set([
  "Product.VersionMismatch", "EggUnitConversion.VersionMismatch", "Concurrency conflict",
]);
const isVersionConflict = (err: unknown) =>
  err instanceof ApiError && err.status === 409 && VERSION_CONFLICT_TITLES.has(err.title);

function errorMessage(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// #97 (part 1): the product catalog — what the farm sells. Egg products map to
// an egg grade; sales screens switch from raw grades to products in part 2.
// No hard delete — future sold lines reference products forever.
export function ProductsPage() {
  const { t } = useTranslation("products");
  const fmt = useFormat();
  const { t: tc } = useTranslation("common");
  const { isAdmin } = useAuth();
  const [products, setProducts] = useState<Product[] | null>(null);
  const [grades, setGrades] = useState<EggGrade[]>([]);
  const [conversions, setConversions] = useState<EggUnitConversion[]>([]);
  // #908 — Products stays one route with two explicit tabs, both using the
  // table+inspector layout; each tab keeps its own selection.
  const [tab, setTab] = useState<"products" | "packedUnits">("products");
  const [selectedProductId, setSelectedProductId] = useState<string | null>(null);
  const [selectedConvId, setSelectedConvId] = useState<string | null>(null);
  // #908 — clears each tab's selection if the row it names ever leaves its
  // catalog (neither tab removes a row today, but the same shape is wired
  // uniformly across all five setup lists).
  useClampSelection(products?.map((p) => p.id) ?? [], selectedProductId, setSelectedProductId);
  useClampSelection(conversions.map((c) => c.id), selectedConvId, setSelectedConvId);
  // #703 — the flight guard (#236), the per-place message slots (#479) and the
  // dialog-session generation (#477 part 2) come from one shared hook; this
  // screen keeps only its idempotency-key and refresh discipline below, and
  // says which scopes own a dialog.
  const { busy, isPending, errors, run, openDialog, dismissDialog } = useDialogAction(DIALOG_SCOPES);
  const setPageError = errors.setPage;

  // create form (F131: in a dialog)
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState("");
  const [unit, setUnit] = useState("Dozen");
  const [gradeId, setGradeId] = useState("");
  const [price, setPrice] = useState("");
  const [notes, setNotes] = useState("");

  // edit (products) — dialog seeded from the row
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editVersion, setEditVersion] = useState(0);
  const [editName, setEditName] = useState("");
  const [editUnit, setEditUnit] = useState("Dozen");
  const [editGradeId, setEditGradeId] = useState("");
  const [editPrice, setEditPrice] = useState("");
  const [editNotes, setEditNotes] = useState("");

  // edit (conversions) — dialog seeded from the row
  const [editingConvId, setEditingConvId] = useState<string | null>(null);
  const [editConvVersion, setEditConvVersion] = useState(0);
  const [editEggs, setEditEggs] = useState(1);
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
    return { products: p, conversions: c };
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

  // The write discipline every mutation on this screen shares: the write under
  // its idempotency key, the list refresh, and only THEN the key rotation — if
  // the refresh throws, the key survives and a retry replays the idempotent
  // write instead of duplicating it. All three are facts about the world: they
  // run whether or not the dialog that started them is still on screen (#703).
  // Called INSIDE `run`; anything it throws lands in the slot `run` was given.
  async function commit(keyScope: string, action: (key: string) => Promise<unknown>): Promise<void> {
    await action(keyFor(keyScope));
    await refresh();
    clearKey(keyScope);
  }

  // Dismissal empties this dialog's slot and mutes the attempt still out, so a
  // late failure is not reported against a session the user reopened.
  const closeCreate = () => { setCreating(false); dismissDialog("create"); };
  const closeEdit = () => { setEditingId(null); dismissDialog("edit"); };
  const closeEditConversion = () => { setEditingConvId(null); dismissDialog("edit-conversion"); };

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    // #703 review r2 — a submit while another action is in flight is SKIPPED by
    // `run`'s guard, so it must not even `beginAttempt`: the pre-run beginAttempt
    // below fires regardless, and un-muting here lets an in-flight ABANDONED
    // attempt's late FAILURE surface against this dialog. Enter bypasses the
    // disabled submit button, so guard it explicitly.
    if (busy) return;
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
    await run("create", async (current) => {
      await commit("create-product", (key) =>
        createProduct({
          name,
          productType: "Egg",
          defaultUnit: unit,
          defaultPriceMinorUnits: priceMinor,
          eggGradeId: gradeId,
          notes: notes.trim() || null,
        }, key));
      // Superseded: the product exists and the catalog shows it, but the form
      // and its dialog belong to whatever session is on screen now (#703).
      if (!current()) return;
      setName("");
      setPrice("");
      setNotes("");
      setCreating(false);
    });
  }

  // Opening a dialog is a session edge (#703): `openDialog` ends whatever
  // session that scope had on screen and mutes its still-out attempt, so a late
  // success cannot touch the dialog now opening. The backdrop keeps a mouse off
  // the row buttons underneath, but #480 established it does not stop a screen
  // reader's virtual cursor — the same door the per-dialog map existed for.
  // `closeCreate`/`closeEditConversion` end the OTHER two sessions this open
  // displaces; `openDialog("edit")` ends any prior edit session unconditionally.
  function startEdit(p: Product) {
    closeCreate();
    closeEditConversion();
    openDialog("edit");
    setEditingId(p.id);
    setEditVersion(p.version);
    setEditName(p.name);
    setEditUnit(p.defaultUnit);
    setEditGradeId(p.eggGradeId ?? "");
    setEditPrice(p.defaultPriceMinorUnits === null
      ? ""
      : (p.defaultPriceMinorUnits / 10 ** p.currencyMinorUnit).toFixed(p.currencyMinorUnit));
    setEditNotes(p.notes ?? "");
  }

  // Same session-edge rule as startEdit, for the conversion dialog's scope.
  function startEditConversion(c: EggUnitConversion) {
    closeCreate();
    closeEdit();
    openDialog("edit-conversion");
    setEditingConvId(c.id);
    setEditConvVersion(c.version);
    setEditEggs(c.eggsPerUnit);
    setEditConvActive(c.active);
  }

  async function onSaveEdit(e: FormEvent) {
    e.preventDefault();
    const id = editingId;
    if (id === null) return;
    // #703 review r2 — see onCreate: a skipped-while-busy submit must not
    // beginAttempt (Enter bypasses the disabled button).
    if (busy) return;
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
    // The run scope is the dialog's; the idempotency key stays per product. The
    // superseded gate is UNREACHABLE here (the row edit button is disabled while
    // the write is in flight) but kept for INV-1 consistency and defence.
    await run("edit", async (current) => {
      try {
        await commit(`update:${id}`, (key) =>
          updateProduct(id, {
            version: editVersion,
            name: editName,
            defaultUnit: editUnit,
            defaultPriceMinorUnits: priceMinor,
            eggGradeId: editGradeId,
            notes: editNotes.trim() || null,
          }, key));
      } catch (err) {
        if (!isVersionConflict(err)) throw err;
        // Another admin saved first: rebind the dialog to their values, as
        // Expenses and History do. The key goes too — the next save is a new
        // request with a new base version.
        clearKey(`update:${id}`);
        const fresh = (await refresh()).products.find((p) => p.id === id);
        if (fresh) startEdit(fresh);
        // startEdit re-opened the dialog's session, which mutes its slot.
        errors.beginAttempt("edit");
        throw new Error(i18n.t("products:conflictRebindMessage"));
      }
      if (!current()) return;
      setEditingId(null);
    });
  }

  async function onSaveConversion(e: FormEvent) {
    e.preventDefault();
    const id = editingConvId;
    if (id === null) return;
    // Superseded gate is unreachable here too (row edit button disabled while
    // busy); kept for consistency. Key stays per conversion.
    await run("edit-conversion", async (current) => {
      try {
        await commit(`conv:${id}`, (key) =>
          updateEggUnitConversion(id, {
            version: editConvVersion, eggsPerUnit: editEggs, active: editConvActive,
          }, key));
      } catch (err) {
        if (!isVersionConflict(err)) throw err;
        // Same rebind as the product dialog above.
        clearKey(`conv:${id}`);
        const fresh = (await refresh()).conversions.find((c) => c.id === id);
        if (fresh) startEditConversion(fresh);
        errors.beginAttempt("edit-conversion");
        throw new Error(i18n.t("products:packedUnitConflictRebindMessage"));
      }
      if (!current()) return;
      setEditingConvId(null);
    });
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
  const selectedProduct = products.find((p) => p.id === selectedProductId) ?? null;

  // #908 — shared between the row's own Actions cell and the inspector's
  // actions, so both call sites stay one implementation.
  function renderProductActions(p: Product) {
    return {
      primary: <button className="link" disabled={busy} onClick={() => startEdit(p)}>{t("editButton")}</button>,
      secondary: !p.active && (
        <BusyButton variant="text" sx={CONSOLE_LINK_SX} disabled={busy} busy={isPending(`act:${p.id}`)}
          onClick={() => void run(`act:${p.id}`, () => commit(`act:${p.id}`, (key) => activateProduct(p.id, key)))}>
          {t("activateButton")}
        </BusyButton>
      ),
      destructive: p.active && (
        <BusyButton variant="text" sx={CONSOLE_DESTRUCTIVE_LINK_SX} disabled={busy} busy={isPending(`deact:${p.id}`)}
          onClick={() => void run(`deact:${p.id}`, () => commit(`deact:${p.id}`, (key) => deactivateProduct(p.id, key)))}>
          <TriangleAlert size={14} aria-hidden /> {t("deactivateButton")}
        </BusyButton>
      ),
    };
  }

  function renderConversionActions(c: EggUnitConversion) {
    return c.unitCode === "Individual" ? (
      <span className="muted">{t("alwaysOneMessage")}</span>
    ) : (
      <button className="link" disabled={busy} onClick={() => startEditConversion(c)}>{t("editButton")}</button>
    );
  }

  return (
    <section>
      <div className="page-head">
        <h2>{t("title")}</h2>
        {/* #655 — withheld while the empty state below is offering this exact
            same action, so there is one "New product" button on screen.
            #908 — Packed units has no create action of its own, so this only
            ever appears on the Products tab. */}
        {tab === "products" && isAdmin && products.length > 0 && (
          <button type="button" onClick={() => { closeEdit(); closeEditConversion(); openDialog("create"); setCreating(true); }}>
            <Plus size={16} aria-hidden /> {t("newProductButton")}
          </button>
        )}
      </div>
      <p className="muted">
        {tab === "products" ? t("intro") : t("packedUnitsIntro")}
      </p>

      {/* #908 — Concept B: Products stays one route with Products first,
          Packed units second, both using the same table+inspector layout. */}
      <Tabs value={tab} onChange={(_, value: "products" | "packedUnits") => setTab(value)}
        aria-label={t("title")} sx={{ borderBottom: "1px solid var(--rule)", mb: 2 }}>
        <Tab id="products-tab" aria-controls="products-tabpanel" value="products" label={t("title")} />
        <Tab id="packed-units-tab" aria-controls="packed-units-tabpanel" value="packedUnits" label={t("packedUnitsHeading")} />
      </Tabs>

      {/* #479 — unconditional: each dialog now renders its own failure through
          its own slot (DialogError below), so nothing here can be a stale copy
          of a dialog's message; this is only ever the page's own. */}
      {errors.page && <p className="error" role="alert">{errors.page}</p>}

      <ProductDialogs
        creating={creating} editingProduct={editingProduct} editingConv={editingConv}
        isAdmin={isAdmin} busy={busy} isPending={isPending} errors={errors}
        grades={grades} currency={currency}
        name={name} setName={setName} unit={unit} setUnit={setUnit}
        gradeId={gradeId} setGradeId={setGradeId} price={price} setPrice={setPrice}
        notes={notes} setNotes={setNotes}
        editName={editName} setEditName={setEditName} editUnit={editUnit} setEditUnit={setEditUnit}
        editGradeId={editGradeId} setEditGradeId={setEditGradeId} editPrice={editPrice} setEditPrice={setEditPrice}
        editEggs={editEggs} setEditEggs={setEditEggs} editConvActive={editConvActive} setEditConvActive={setEditConvActive}
        closeCreate={closeCreate} closeEdit={closeEdit} closeEditConversion={closeEditConversion}
        onCreate={onCreate} onSaveEdit={onSaveEdit} onSaveConversion={onSaveConversion}
      />


      <Box role="tabpanel" id="products-tabpanel" aria-labelledby="products-tab" hidden={tab !== "products"}>
        {products.length === 0 ? (
          <EmptyState icon={Package} message={t("noProductsMessage")}
            action={isAdmin ? { label: t("newProductButton"), onClick: () => { closeEdit(); closeEditConversion(); openDialog("create"); setCreating(true); } } : undefined} />
        ) : (
          <ListInspectorPane
            tableLabel={t("title")}
            onClearSelection={() => setSelectedProductId(null)}
            table={(
              <LedgerTableContainer scrollHint="columnsAndRows">
                <Table size="small" sx={isAdmin ? { "& :is(th, td):last-child": { display: { xs: "none", md: "table-cell" } } } : undefined}>
                  <TableHead>
                    <TableRow>
                      <TableCell sx={STICKY_TABLE_HEAD_SX}>{t("nameHeader")}</TableCell>
                      <TableCell sx={STICKY_TABLE_HEAD_SX}>{t("gradeHeader")}</TableCell>
                      <TableCell sx={STICKY_TABLE_HEAD_SX}>{t("soldPerHeader")}</TableCell>
                      <TableCell align="right" sx={STICKY_TABLE_HEAD_SX}>{t("defaultPriceHeader")}</TableCell>
                      <TableCell sx={STICKY_TABLE_HEAD_SX}>{t("statusHeader")}</TableCell>
                      {isAdmin && <TableCell sx={STICKY_TABLE_HEAD_SX}>{tc("actions")}</TableCell>}
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {products.map((p) => (
                      <TableRow key={p.id} className={p.active ? undefined : "muted"}
                        {...selectableRowProps(p.id === selectedProductId, () => setSelectedProductId(p.id))}>
                        <TableCell sx={NOWRAP}>
                          {/* The Tooltip trigger is this leaf span, not the
                              TableCell itself. */}
                          <Tooltip title={p.notes ?? undefined} describeChild>
                            <Box component="span">{p.name}</Box>
                          </Tooltip>
                        </TableCell>
                        <TableCell sx={NOWRAP}>{gradeName(p.eggGradeId)}</TableCell>
                        <TableCell sx={NOWRAP}>{p.defaultUnit}</TableCell>
                        <TableCell align="right" sx={NOWRAP}>{p.defaultPriceMinorUnits === null
                          ? "—"
                          : fmt.money(p.defaultPriceMinorUnits, p.currencyCode, p.currencyMinorUnit)}</TableCell>
                        <TableCell sx={NOWRAP}><StatusBadge status={p.active ? "Active" : "Inactive"} label={statusLabel(p.active ? "Active" : "Inactive")} /></TableCell>
                        {isAdmin && (
                          <TableCell sx={NOWRAP}>
                            <Stack direction="row" spacing={1} sx={{ flexWrap: "nowrap", alignItems: "center" }}>
                              {Object.entries(renderProductActions(p)).map(([key, action]) => <Fragment key={key}>{action}</Fragment>)}
                            </Stack>
                          </TableCell>
                        )}
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </LedgerTableContainer>
            )}
            inspector={(
              <RecordInspector
                ariaLabel={tc("inspectorLabel", { entity: t("entitySingular") })}
                title={selectedProduct?.name}
                emptyMessage={tc("inspectorEmptyPrompt")}
                fields={selectedProduct ? [
                  { label: t("gradeHeader"), value: gradeName(selectedProduct.eggGradeId) },
                  { label: t("soldPerHeader"), value: selectedProduct.defaultUnit },
                  {
                    label: t("defaultPriceHeader"),
                    value: selectedProduct.defaultPriceMinorUnits === null
                      ? "—"
                      : fmt.money(selectedProduct.defaultPriceMinorUnits, selectedProduct.currencyCode, selectedProduct.currencyMinorUnit),
                  },
                  { label: t("statusHeader"), value: <StatusBadge status={selectedProduct.active ? "Active" : "Inactive"} label={statusLabel(selectedProduct.active ? "Active" : "Inactive")} /> },
                  ...(selectedProduct.notes ? [{ label: t("notesLabel"), value: selectedProduct.notes }] : []),
                ] : undefined}
                actions={isAdmin && selectedProduct ? renderProductActions(selectedProduct) : undefined}
              />
            )}
          />
        )}
      </Box>

      <PackedUnitsPanel hidden={tab !== "packedUnits"} conversions={conversions} isAdmin={isAdmin}
        selectedId={selectedConvId} onSelect={setSelectedConvId} renderActions={renderConversionActions} />
    </section>
  );
}
