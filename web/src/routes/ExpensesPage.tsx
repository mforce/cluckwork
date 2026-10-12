import { useCallback, useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { FilterX, Plus, Receipt } from "lucide-react";
import {
  Box, Button, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography, useMediaQuery,
} from "@mui/material";
import {
  listExpenseCategories, listExpenses, listFlocks,
} from "../api/cluckwork";
import { ApiError } from "../api/client";
import type { Expense, ExpenseCategory, Flock } from "../api/cluckwork";
import { useFormat } from "../farm/useFormat";
import { FarmDate } from "../components/FarmDate";
import { FieldConsole, ConsoleSubhead, ConsoleSummary, CONSOLE_LINK_SX, CONSOLE_SPLIT_SX, CONSOLE_RAIL_SX } from "../components/FieldConsole";
import { LedgerTableContainer } from "../components/FieldConsole";
import { ProvenanceCell } from "../components/ProvenanceCell";
import { PhoneLedgerList, PhoneLedgerRow } from "../components/PhoneLedger";
import { EmptyState } from "../components/EmptyState";
import { FilterBar, FilterDateField } from "../components/FilterBar";
import type { PickerSnapshot } from "../components/NamedEntityPicker";
import { useDialogAction } from "../components/useDialogAction";
import { usePagedList } from "../components/usePagedList";
import { useFarm, useFarmToday } from "../farm/useFarm";
import { newId } from "../lib/ids";
import { Link } from "react-router";
import { MD_UP_QUERY } from "../lib/breakpoints";
import { ExpenseCategoriesPanel } from "./expenses/ExpenseCategoriesPanel";
import { ExpenseEditDialog } from "./expenses/ExpenseEditDialog";
import { ExpenseEntryPanel } from "./expenses/ExpenseEntryPanel";
import { ExpensePhoneDialogs } from "./expenses/ExpensePhoneDialogs";
import { useExpenseEdit } from "./expenses/useExpenseEdit";

function errText(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

const PAGE = 100;
const NOWRAP = { whiteSpace: "nowrap" as const };

// The scopes that own a dialog (#703). `run` routes a failure by this and gates
// a success by it; a scope outside the list — the record-expense form on the
// page, the category toggles in the panel — reports to the page and is never
// superseded.
const DIALOG_SCOPES = ["edit", "add-category"] as const;

// #87 — basic expenses (spec §16 cut): categories + recording + monthly view.
// Admin-only end to end: the route hides for workers and every endpoint
// carries the Admin policy — money data, unlike the production screens.
export function ExpensesPage() {
  const isDesktop = useMediaQuery(MD_UP_QUERY);
  const { t } = useTranslation("expenses");
  const fmt = useFormat();
  const { t: tc } = useTranslation("common");

  // Farm-local, not browser-local: since #35 the API judges "is this date in
  // the future?" against the FARM's day, so the pickers must agree (#123).
  const today = useFarmToday();
  const { farm } = useFarm();
  const [categories, setCategories] = useState<ExpenseCategory[]>([]);
  const [flocks, setFlocks] = useState<Flock[]>([]);
  const [message, setMessage] = useState<string | null>(null);
  // #703 — the flight guard (#236), the per-place message slots (#479: the
  // record-expense form sits on the page, same as the mount read and the
  // category-toggle writes; "add-category" and the correction dialog get
  // their own) and the dialog-session generation (#477 part 2) come from one
  // shared hook; this screen keeps only its idempotency-key discipline below,
  // and says which scopes own a dialog. The page's message clears as each
  // attempt starts, exactly where the old wrapper cleared it.
  const dialog = useDialogAction(
    DIALOG_SCOPES,
    { onAttempt: () => setMessage(null) },
  );
  const { busy, errors, openDialog } = dialog;
  const setPageError = errors.setPage;

  // filters
  // #667 — a from/to pair, like every sibling list screen. It DEFAULTS to the
  // current farm month rather than opening blank as the siblings do, and that
  // divergence is deliberate: this screen shows a period TOTAL, so opening
  // blank would silently change the default view from "this month's spend" to
  // "every expense ever recorded, and a total to match". The user can widen or
  // clear it; the default preserves what shipped.
  const monthStart = `${today.slice(0, 7)}-01`;
  const monthEnd = (() => {
    const [y, mo] = today.slice(0, 7).split("-").map(Number);
    const last = new Date(Date.UTC(y, mo, 0)).getUTCDate();
    return `${today.slice(0, 7)}-${String(last).padStart(2, "0")}`;
  })();
  const [from, setFrom] = useState(monthStart);
  const [to, setTo] = useState(monthEnd);
  const [filterCategory, setFilterCategory] = useState("");

  // #679 — "clear" on this screen means RESTORE THE DEFAULT, not blank the
  // range (owner decision, 2026-09-05). Blanking would leave periodTotalLabel
  // describing an unbounded total across every expense ever recorded, which is
  // the framing #667 deliberately declined to make the default. All-time stays
  // reachable, but as its own labelled action in the empty state below rather
  // than as the meaning of this button.
  const isFiltered = from !== monthStart || to !== monthEnd || filterCategory !== "";
  const resetFilters = () => {
    setFrom(monthStart);
    setTo(monthEnd);
    setFilterCategory("");
  };
  const showAllTime = () => {
    setFrom("");
    setTo("");
    setFilterCategory("");
  };

  // add form
  const [date, setDate] = useState(today);
  const [categoryId, setCategoryId] = useState("");
  const [description, setDescription] = useState("");
  const [amount, setAmount] = useState("");
  // #512 (T028/T038) — the optional flock is committed through FlockPicker.
  // A blank (account-wide) selection is VALID: the picker's `canSubmit` is
  // the write guard (exploring/unavailable blocks it), never "must have a
  // flock". `addFlock` mirrors the engine's committed entity via onCommit and
  // is reset explicitly on success (the page never syncs it back — no
  // controlled generation — so the picker's own discovery lifecycle is never
  // disturbed).
  const [addFlock, setAddFlock] = useState<Flock | null>(null);
  // Bumped after the post-success reset so a later Escape cannot resurrect
  // the just-saved flock (engine controlled-sync, US2).
  const [addFlockGen, setAddFlockGen] = useState(0);
  // The INITIAL snapshot is the page's own honest state: no committed entity
  // and no exact read issued yet, so the write is withheld (canSubmit false)
  // until the picker's real snapshot lands. The picker is OPTIONAL, so once
  // it initializes the blank selection submits (a valid account-wide choice).
  const [addFlockSnapshot, setAddFlockSnapshot] = useState<PickerSnapshot<Flock>>({
    committed: null, selectionPhase: "uninitialized", exploring: false, canSubmit: false,
  });
  const [addFlockPickerOpen, setAddFlockPickerOpen] = useState(false);
  const [note, setNote] = useState("");

  // category management
  const [showCategories, setShowCategories] = useState(false);
  const [addOpen, setAddOpen] = useState(false);
  const [filterOpen, setFilterOpen] = useState(false);
  const [details, setDetails] = useState<Expense | null>(null);
  const [addingCategory, setAddingCategory] = useState(false); // F131: in a dialog
  const [newCategoryName, setNewCategoryName] = useState("");

  // Stable idempotency keys per logical mutation. Version-guarded edits rotate
  // on ANY server response (the version base prevents double-apply); only a
  // transport failure keeps the key for an exact replay (HistoryPage pattern).
  const keys = useRef(new Map<string, string>());
  const keyFor = (scope: string) => {
    const existing = keys.current.get(scope);
    if (existing) return existing;
    const fresh = newId();
    keys.current.set(scope, fresh);
    return fresh;
  };
  const clearKey = (scope: string) => keys.current.delete(scope);
  const settleKey = (scope: string, err?: unknown) => {
    if (err === undefined || err instanceof ApiError) clearKey(scope);
  };

  // offset 0 replaces the page (fresh view after filters/mutations); a larger
  // offset appends — months can exceed one page and every row must stay
  // reachable for correction (codex review of #88). The total always covers
  // the WHOLE filtered period regardless of paging.
  // #469 — this list had no request sequencing, on the screen where it hurts
  // most: a failed filter change used to leave the PREVIOUS month's rows and
  // total under the new month's picker, reading as a legitimate figure for a
  // period it never described. The total rides as page metadata so it is
  // ticket-protected exactly like the rows and cleared with them.
  const expenses = usePagedList<Expense, { total: number; code: string; minor: number }>({
    fetchPage: useCallback(async (offset: number, limit: number) => {
      const list = await listExpenses({
        from: from || undefined,
        to: to || undefined,
        categoryId: filterCategory || undefined,
        limit,
        offset,
      });
      return {
        items: list.items,
        meta: {
          total: list.totalMinorUnits,
          code: list.currencyCode,
          minor: list.currencyMinorUnit,
        },
      };
      // INV-3 — every value the request body uses. #469's ticket discipline
      // keys the whole "previous window's total under the new window's control"
      // protection on this identity.
    }, [from, to, filterCategory]),
    pageSize: PAGE,
  });
  // MONEY SCALE — never guessed, always the freshest authority available.
  // Three review rounds landed on this order, each for its own failure:
  //
  //   1. the CURRENT list response, because its envelope carries the
  //      account's live currency (the endpoint reads the account per
  //      request) — while `farm` is only the snapshot this tab booted with,
  //      so a currency changed elsewhere reaches this screen through the
  //      list first and the snapshot would convert at a retired scale;
  //   2. the last list scale seen, retained across a failed load so a blip
  //      cannot un-know a scale that was already established;
  //   3. the farm snapshot, for the case where no list has ever landed;
  //   4. nothing — and then the form REFUSES to record (see scaleKnown),
  //      because denominating a typed amount at an assumed two decimals is
  //      how a 3-decimal farm stores 1.000 as 100 minor units.
  const lastListScale = useRef<{ code: string; minor: number } | null>(null);
  if (expenses.meta !== null) {
    lastListScale.current = { code: expenses.meta.code, minor: expenses.meta.minor };
  }
  const currency = expenses.meta !== null
    ? { code: expenses.meta.code, minor: expenses.meta.minor }
    // Retained across a failed load, so a blip cannot un-know the scale.
    : lastListScale.current
      ?? (farm !== null
        ? { code: farm.currencyCode, minor: farm.currencyMinorUnit }
        : null);
  const scaleKnown = currency !== null;
  // Display-only fallbacks; nothing below CONVERTS with these.
  const currencyCode = currency?.code ?? "";
  const currencyMinor = currency?.minor ?? 2;

  useEffect(() => {
    Promise.all([
      listExpenseCategories({ includeInactive: true }),
      listFlocks({ includeArchived: true, limit: 500 }),
    ])
      .then(([c, f]) => {
        setCategories(c);
        setFlocks(f);
      })
      .catch((err) => setPageError(errText(err)));
  }, [setPageError]);


  const categoryName = (id: string) =>
    categories.find((c) => c.id === id)?.name ?? id.slice(0, 8);
  // #512 US4 (T049/T051) — the row renders the flock name its OWN record
  // carries (the endpoint's one scoped bulk read per page), never the
  // picker's capped discovery results and never an id fragment
  // (contracts/http-api.md: "Required names are never replaced with
  // identifier fragments"). `flockId === null` is the deliberate
  // account-wide expense; a non-null `flockId` with a null `flockName` is
  // the defensive out-of-scope case.
  const rowFlockName = (x: Expense) =>
    x.flockId === null ? "—" : (x.flockName ?? t("flockUnavailable"));
  const activeCategories = categories.filter((c) => c.active);

  // The key policy every write on this screen shares (see settleKey): a server
  // response — success or ApiError — spends the key, a transport failure keeps
  // it for an exact replay. It is a fact about the world, so it runs whether
  // or not the dialog that started the write is still on screen (#703 — the
  // superseded-safe rule). The flight guard, the failure routing and the
  // success gate are the hook's; this helper is called INSIDE `run`, and what
  // it rethrows lands in the slot `run` was given.
  async function commit(keyScope: string, fn: () => Promise<void>): Promise<void> {
    try {
      await fn();
    } catch (err) {
      settleKey(keyScope, err);
      throw err;
    }
    settleKey(keyScope);
  }
  const edit = useExpenseEdit({
    dialog,
    commit,
    keyFor,
    settleKey,
    expenses,
    flocks,
    categories,
    activeCategories,
    setMessage,
  });
  const { startEdit } = edit;

  const filters = (
      <FilterBar>
        {/* Do not cap at today: the default end date is month-end. */}
        <FilterDateField label={t("fromLabel")} value={from} onChange={(e) => setFrom(e.target.value)} />
        <FilterDateField label={t("toLabel")} value={to} onChange={(e) => setTo(e.target.value)} />
        <TextField
          select
          label={t("categoryLabel")}
          value={filterCategory}
          size="small"
          // Shrink the label so it does not overlap the empty-value placeholder.
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
          onChange={(e) => setFilterCategory(e.target.value)}
        >
          <option value="">{t("allCategoriesOption")}</option>
          {categories.map((c) => (
            <option key={c.id} value={c.id}>{c.name}{c.active ? "" : t("deactivatedSuffix")}</option>
          ))}
        </TextField>
        <Button variant="outlined" color="inherit" sx={{ borderRadius: "4px" }} onClick={resetFilters}>{tc("clearFiltersButton")}</Button>
      </FilterBar>
  );
  const categoriesPanel = (
    <ExpenseCategoriesPanel
      dialog={dialog}
      commit={commit}
      keyFor={keyFor}
      setMessage={setMessage}
      setCategories={setCategories}
      setAddingCategory={setAddingCategory}
      addingCategory={addingCategory}
      newCategoryName={newCategoryName}
      setNewCategoryName={setNewCategoryName}
      categories={categories}
    />
  );
  const entryPanel = (
    <ExpenseEntryPanel
      dialog={dialog}
      commit={commit}
      keyFor={keyFor}
      expenses={expenses}
      currency={currency}
      setMessage={setMessage}
      setAddOpen={setAddOpen}
      setShowCategories={setShowCategories}
      isDesktop={isDesktop}
      today={today}
      date={date} setDate={setDate}
      categoryId={categoryId} setCategoryId={setCategoryId}
      description={description} setDescription={setDescription}
      amount={amount} setAmount={setAmount}
      note={note} setNote={setNote}
      addFlock={addFlock} setAddFlock={setAddFlock}
      addFlockGen={addFlockGen} setAddFlockGen={setAddFlockGen}
      addFlockSnapshot={addFlockSnapshot} setAddFlockSnapshot={setAddFlockSnapshot}
      addFlockPickerOpen={addFlockPickerOpen} setAddFlockPickerOpen={setAddFlockPickerOpen}
      activeCategories={activeCategories}
      scaleKnown={scaleKnown}
      currencyCode={currencyCode} currencyMinor={currencyMinor}
    />
  );

  return (
    <FieldConsole>
      <Stack component="header" direction="row" sx={{ justifyContent: "space-between", gap: 1, mb: { xs: 1, md: 2 } }}>
        <Box><Typography variant="h2">{t("title")}</Typography>
          {isDesktop && <Typography component="p" sx={{ m: 0, maxWidth: 700, color: "text.secondary", fontSize: "13px", lineHeight: 1.45 }}>{t("intro")}</Typography>}
        </Box>
        <Button variant="contained" startIcon={!isDesktop || !showCategories ? <Plus size={16} aria-hidden="true" /> : undefined} sx={{ minHeight: 44, borderRadius: "4px", flexShrink: 0, alignSelf: "flex-start" }} onClick={isDesktop ? () => setShowCategories((v) => !v) : () => setAddOpen(true)}>
          {isDesktop ? showCategories ? t("hideCategoriesButton") : t("manageCategoriesButton") : t("addButton")}
        </Button>
      </Stack>
      {!isDesktop && <Typography component="p" sx={{ m: 0, maxWidth: 700, color: "text.secondary", fontSize: "13px", lineHeight: 1.45 }}>{t("intro")}</Typography>}
      {isDesktop ? <ConsoleSummary label={t("contextLabel")} items={[
        { label: t("periodHeading"), value: !expenses.reloading && expenses.meta !== null ? fmt.money(expenses.meta.total, currencyCode, currencyMinor) : "—" },
        { label: t("fromLabel"), value: from ? fmt.date(from) : "—" },
        { label: t("toLabel"), value: to ? fmt.date(to) : "—" },
        { label: t("categoryLabel"), value: categories.find((category) => category.id === filterCategory)?.name ?? t("allCategoriesOption") },
      ]} /> : <Box component="dl" aria-label={t("contextLabel")} sx={{ display: "flex", gap: .75, py: 1, my: 1.5, borderTop: "1px solid var(--rule)", borderBottom: "1px solid var(--rule)", fontSize: ".75rem" }}>
        <dt>{t("periodHeading")}</dt><Box component="dd" sx={{ m: 0, fontWeight: 700 }}>{!expenses.reloading && expenses.meta !== null ? fmt.money(expenses.meta.total, currencyCode, currencyMinor) : "—"}</Box>
      </Box>}

      {!isDesktop && <ExpensePhoneDialogs
        dialog={dialog}
        edit={edit}
        filters={filters}
        categoriesPanel={categoriesPanel}
        entryPanel={entryPanel}
        from={from}
        to={to}
        filterOpen={filterOpen} setFilterOpen={setFilterOpen}
        addOpen={addOpen} setAddOpen={setAddOpen}
        showCategories={showCategories} setShowCategories={setShowCategories}
        activeCategories={activeCategories}
        scaleKnown={scaleKnown}
        addFlockSnapshot={addFlockSnapshot}
        message={message}
        details={details} setDetails={setDetails}
        categories={categories}
        filterCategory={filterCategory}
        categoryName={categoryName}
        rowFlockName={rowFlockName}
      />}

      {isDesktop && filters}

      {isDesktop && showCategories && categoriesPanel}

      {isDesktop && <Box sx={{ ...CONSOLE_SPLIT_SX, gridTemplateColumns: { xs: "minmax(0, 1fr)", md: "minmax(0, .9fr) minmax(0, 1.1fr)" } }}>
      {entryPanel}
      <Box component="aside" sx={CONSOLE_RAIL_SX}
        aria-label={!expenses.reloading && expenses.meta !== null
          ? t("periodTotalLabel", { amount: fmt.money(expenses.meta.total, currencyCode, currencyMinor) })
          : undefined}>
        {!expenses.reloading && expenses.meta !== null && (
          <>
            <Typography component="p" variant="body2">{t("periodHeading")}</Typography>
            <Typography component="p" variant="body2" sx={{
              fontSize: "2rem", fontWeight: 700, fontVariantNumeric: "tabular-nums", my: 1,
              // #835/#948 — one of the stat figures; clears the root's text-cut pin.
              fontVariationSettings: "normal",
            }}>
              {fmt.money(expenses.meta.total, currencyCode, currencyMinor)}
            </Typography>
            <Typography sx={{ fontSize: ".8rem" }}>{t("wholePeriod")}</Typography>
          </>
        )}
      </Box>
      </Box>}

      {(isDesktop || !addOpen) && errors.page && <p className="error" role="alert">{errors.page}</p>}
      {(isDesktop || !addOpen) && message && <p className="success" role="status">{message}</p>}

      <ExpenseEditDialog edit={edit} dialog={dialog} today={today} />

      {expenses.error && <p className="error" role="alert">{expenses.error}</p>}

      <ConsoleSubhead title={t("ledgerHeading")} caption={t("ledgerCaption")} />
      {expenses.rows === null || expenses.reloading ? (
        <p className="muted">{tc("loading")}</p>
      ) : expenses.rows.length === 0 ? (
        (from || to || filterCategory)
          ? <EmptyState icon={FilterX} message={t("noExpensesMatch")}
              action={isFiltered
                ? undefined
                : { label: t("showAllTimeButton"), onClick: showAllTime }} />
          : <EmptyState icon={Receipt} message={t("noExpensesMessage")} />
      ) : (
        isDesktop ? <LedgerTableContainer>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell sx={NOWRAP}>{t("dateHeader")}</TableCell>
                <TableCell sx={NOWRAP}>{t("categoryHeader")}</TableCell>
                <TableCell>{t("descriptionHeader")}</TableCell>
                <TableCell align="right" sx={NOWRAP}>{t("amountHeader")}</TableCell>
                <TableCell sx={NOWRAP}>{t("flockHeader")}</TableCell>
                <TableCell>{t("noteHeader")}</TableCell>
                <TableCell sx={NOWRAP}>{tc("recordHistoryHeader")}</TableCell>
                <TableCell></TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {expenses.rows.map((x) => (
                <TableRow key={x.id}>
                  <TableCell sx={NOWRAP}><FarmDate iso={x.date} /></TableCell>
                  <TableCell sx={NOWRAP}>{categoryName(x.expenseCategoryId)}</TableCell>
                  <TableCell>{x.description}</TableCell>
                  <TableCell align="right" sx={NOWRAP}>{fmt.money(x.amountMinorUnits, x.currencyCode, x.currencyMinorUnit)}</TableCell>
                  <TableCell sx={NOWRAP}>{rowFlockName(x)}</TableCell>
                  <TableCell>{x.note ?? "—"}</TableCell>
                  <ProvenanceCell history={x} />
                  <TableCell sx={NOWRAP}>
                    <Link className="link" to={`/audit?entityId=${x.id}`}>
                      {tc("recordHistory.viewHistoryLink")}
                    </Link>
                    <Button size="small" sx={{ ...CONSOLE_LINK_SX, ml: "9px" }} disabled={busy}
                      onClick={() => { openDialog("edit"); startEdit(x); }}>
                      {t("correctButton")}
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </LedgerTableContainer> : <PhoneLedgerList label={t("ledgerHeading")}>
          {expenses.rows.map((x) => <li key={x.id}>
            <PhoneLedgerRow onClick={() => setDetails(x)} date={<FarmDate iso={x.date} />}
              primary={x.description}
              trailing={<strong>{fmt.money(x.amountMinorUnits, x.currencyCode, x.currencyMinorUnit)}</strong>}
              summary={<><strong>{categoryName(x.expenseCategoryId)}</strong>{x.flockId !== null && ` · ${rowFlockName(x)}`}{x.note && ` · ${x.note}`}</>} />
          </li>)}
        </PhoneLedgerList>
      )}
      {expenses.canLoadMore && (
        <button className="link" disabled={busy}
          onClick={() => void expenses.loadMore()}>
          {t("loadMoreButton")}
        </button>
      )}
    </FieldConsole>
  );
}
