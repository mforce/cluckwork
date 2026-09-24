import { Fragment, useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { Plus, TriangleAlert } from "lucide-react";
import {
  Checkbox, DialogActions, FormControlLabel, Stack, Table, TableBody, TableCell,
  TableHead, TableRow, TextField,
} from "@mui/material";
import {
  activateEggGrade, createEggGrade, deactivateEggGrade, listEggGrades, updateEggGrade,
} from "../api/cluckwork";
import type { EggGrade } from "../api/cluckwork";
import { useFormat } from "../farm/useFormat";
import { useAuth } from "../auth/useAuth";
import { BusyButton } from "../components/BusyButton";
import {
  CONSOLE_DESTRUCTIVE_LINK_SX, CONSOLE_LINK_SX, LedgerTableContainer, ListInspectorPane, RecordInspector,
  STICKY_TABLE_HEAD_SX, selectableRowProps, useClampSelection,
} from "../components/FieldConsole";
import { Dialog } from "../components/Dialog";
import { DialogError } from "../components/DialogError";
import { ProvenanceCell } from "../components/ProvenanceCell";
import { StatusBadge } from "../components/StatusBadge";
import { useDialogAction } from "../components/useDialogAction";
import { newId } from "../lib/ids";
import i18n from "../i18n";
import { gradeTypeLabel, statusLabel } from "../i18n/enums";

const GRADE_TYPES = ["Size", "Quality", "Custom"];

// The scopes that own a dialog (#703). A scope outside the list — the row
// activate/deactivate writes — reports to the page and is never superseded.
const DIALOG_SCOPES = ["create", "edit"] as const;

// MUI's auto table layout shrinks any wrappable cell below its content width,
// so short values (names, numbers, chips, actions) are pinned; free text wraps.
const NOWRAP = { whiteSpace: "nowrap" as const };

// F6 (#42): manage the farm's egg grades. No hard delete — grade lines, lots,
// and order items reference grades forever; deactivation only removes a grade
// from capture/order pickers while history keeps rendering its name.
export function GradesPage() {
  const { t } = useTranslation("grades");
  const fmt = useFormat();
  const { t: tc } = useTranslation("common");
  // The grade catalog is configuration — management is admin-only (#73). The
  // nav link hides for workers; a direct URL just renders the list read-only.
  const { isAdmin } = useAuth();
  const [grades, setGrades] = useState<EggGrade[] | null>(null);
  // #908 — the bottom inspector's selection.
  const [selectedId, setSelectedId] = useState<string | null>(null);
  // #908 — clears the selection if the row it names ever leaves the catalog
  // (this screen never removes a grade today, but the same shape is wired
  // uniformly across all five setup lists).
  useClampSelection(grades?.map((g) => g.id) ?? [], selectedId, setSelectedId);
  const selectedGrade = grades?.find((g) => g.id === selectedId) ?? null;
  // #703 — the flight guard (#236), the per-place message slots (#479) and the
  // dialog-session generation (#477 part 2) come from one shared hook; this
  // screen keeps only its idempotency-key and refresh discipline below, and
  // says which scopes own a dialog.
  const { busy, isPending, errors, run, openDialog, dismissDialog } = useDialogAction(DIALOG_SCOPES);
  const setPageError = errors.setPage;

  // create form (F131: lives in a dialog, not a bar above the table)
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState("");
  const [gradeType, setGradeType] = useState("Size");
  const [sortOrder, setSortOrder] = useState(0);
  const [isSaleable, setIsSaleable] = useState(true);

  // edit form — same dialog treatment, opened from the row's edit button
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState("");
  const [editSort, setEditSort] = useState(0);
  const [editSaleable, setEditSaleable] = useState(true);

  // Stable idempotency keys per logical mutation, rotated only after the full
  // action (write + refresh) succeeds — same contract as the other screens.
  const keys = useRef(new Map<string, string>());
  const keyFor = (scope: string) => {
    const existing = keys.current.get(scope);
    if (existing) return existing;
    const fresh = newId();
    keys.current.set(scope, fresh);
    return fresh;
  };
  const clearKey = (scope: string) => keys.current.delete(scope);

  const fetchGrades = () => listEggGrades({ includeInactive: true });

  useEffect(() => {
    fetchGrades()
      .then(setGrades)
      .catch(() => setPageError(i18n.t("grades:loadGradesFailed")));
  }, [setPageError]);

  // The write discipline every mutation on this screen shares: the write under
  // its idempotency key, the list refresh, and only THEN the key rotation — if
  // the refresh throws, the key survives and a retry replays the idempotent
  // write instead of duplicating it. All three are facts about the world: they
  // run whether or not the dialog that started them is still on screen (#703).
  // Called INSIDE `run`; anything it throws lands in the slot `run` was given.
  async function commit(keyScope: string, action: (key: string) => Promise<unknown>): Promise<void> {
    await action(keyFor(keyScope));
    setGrades(await fetchGrades());
    clearKey(keyScope);
  }

  // Opening create ends any edit session on screen (#703): `closeEdit` mutes the
  // edit attempt still out and ends its session, so its late success cannot
  // touch the create dialog now opening. The backdrop stops a mouse reaching the
  // edit trigger, but not a screen reader's virtual cursor (#480), so this
  // displacement is real; `openDialog`/`dismissDialog` handle it unconditionally.
  function openCreate() {
    closeEdit();
    openDialog("create");
    setCreating(true);
  }

  // Dismissal is one of the two session edges (#703): it mutes the attempt still
  // out and ends the session, so a late failure or success cannot act on a
  // session the user reopened.
  function closeCreate() {
    setCreating(false);
    dismissDialog("create");
  }

  function closeEdit() {
    setEditingId(null);
    dismissDialog("edit");
  }

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    await run("create", async (current) => {
      await commit("create-grade", (key) =>
        createEggGrade({ name, gradeType, sortOrder, isSaleable }, key));
      // Superseded: the grade exists and the list shows it, but the form and its
      // dialog belong to whatever session is on screen now (#703).
      if (!current()) return;
      setName("");
      setSortOrder(0);
      setIsSaleable(true);
      setCreating(false);
    });
  }

  function startEdit(g: EggGrade) {
    closeCreate(); // create and edit are mutually exclusive; end any create session
    // Opening is the other session edge (#703): whatever edit session was on
    // screen — this grade's or, reached behind the backdrop by a screen reader's
    // virtual cursor (#480; pi review of #491), another grade's — is over, its
    // attempt muted and its success unable to touch this one. That is what used
    // to need the per-id displacement check and the per-id error slot here.
    openDialog("edit");
    setEditingId(g.id);
    setEditName(g.name);
    setEditSort(g.sortOrder);
    setEditSaleable(g.isSaleable);
  }

  async function onSaveEdit(e: FormEvent) {
    e.preventDefault();
    const id = editingId;
    if (id === null) return;
    // The run scope is the dialog's; the idempotency key stays per grade.
    await run("edit", async (current) => {
      await commit(`update:${id}`, (key) =>
        updateEggGrade(id, { name: editName, sortOrder: editSort, isSaleable: editSaleable }, key));
      if (!current()) return;
      setEditingId(null);
    });
  }

  // #908 — shared between the row's own Actions cell and the inspector's
  // actions, so both call sites stay one implementation.
  function renderActions(g: EggGrade) {
    return {
      primary: isAdmin && (
        <button className="link" disabled={busy} onClick={() => startEdit(g)}>{t("editButton")}</button>
      ),
      secondary: isAdmin && <>
        <Link className="link" to={`/audit?entityId=${g.id}`}>{tc("recordHistory.viewHistoryLink")}</Link>
        {!g.active && (
        <BusyButton variant="text" sx={CONSOLE_LINK_SX} busy={isPending(`activate:${g.id}`)} disabled={busy}
          onClick={() => void run(`activate:${g.id}`, () => commit(`activate:${g.id}`, (key) => activateEggGrade(g.id, key)))}>
          {t("activateButton")}
        </BusyButton>
        )}
      </>,
      destructive: isAdmin && g.active && (
        <BusyButton variant="text" sx={CONSOLE_DESTRUCTIVE_LINK_SX} busy={isPending(`deactivate:${g.id}`)} disabled={busy}
          onClick={() => void run(`deactivate:${g.id}`, () => commit(`deactivate:${g.id}`, (key) => deactivateEggGrade(g.id, key)))}>
          <TriangleAlert size={14} aria-hidden /> {t("deactivateButton")}
        </BusyButton>

      ),
    };
  }

  if (errors.page && grades === null) {
    return <section><h2>{t("loadingTitle")}</h2><p className="error">{errors.page}</p></section>;
  }
  if (grades === null) {
    return <section><h2>{t("loadingTitle")}</h2><p className="muted">{tc("loading")}</p></section>;
  }

  return (
    <section>
      <div className="page-head">
        <h2>{t("title")}</h2>
        {isAdmin && (
          <button type="button" onClick={openCreate}>
            <Plus size={16} aria-hidden /> {t("newGradeButton")}
          </button>
        )}
      </div>
      <p className="muted">
        {t("intro")}
      </p>

      {/* Gated like the inline form was: a role change mid-edit closes it. */}
      <Dialog open={creating && isAdmin} title={t("newGradeDialogTitle")} onClose={closeCreate}
        actions={(
          <DialogActions>
            <button type="button" className="link" onClick={closeCreate}>{tc("cancel")}</button>
            <BusyButton variant="contained" type="submit" busy={isPending("create")} disabled={busy}>{t("addGradeButton")}</BusyButton>
          </DialogActions>
        )}
        formProps={{ onSubmit: onCreate }}
      >
        <Stack spacing={2}>
          <TextField
            label={t("nameLabel")}
            value={name}
            slotProps={{ htmlInput: { maxLength: 50, required: true } }}
            onChange={(e) => setName(e.target.value)}
          />
          <TextField
            select
            label={t("typeLabel")}
            value={gradeType}
            slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}
            onChange={(e) => setGradeType(e.target.value)}
          >
            {GRADE_TYPES.map((gt) => <option key={gt} value={gt}>{gradeTypeLabel(gt)}</option>)}
          </TextField>
          <TextField
            label={t("sortLabel")}
            type="number"
            value={sortOrder}
            onChange={(e) => setSortOrder(Number(e.target.value) || 0)}
            sx={{ maxWidth: "10rem" }}
          />
          <FormControlLabel
            label={t("saleableLabel")}
            control={<Checkbox checked={isSaleable} onChange={(e) => setIsSaleable(e.target.checked)} />}
          />
          <DialogError errors={errors} scope="create" />
        </Stack>
      </Dialog>

      <Dialog open={editingId !== null && isAdmin} title={t("editGradeDialogTitle")} onClose={closeEdit}
        actions={(
          <DialogActions>
            <button type="button" className="link" onClick={closeEdit}>{tc("cancel")}</button>
            <BusyButton variant="contained" type="submit" busy={isPending("edit")} disabled={busy}>
              {tc("save")}
            </BusyButton>
          </DialogActions>
        )}
        formProps={{ onSubmit: onSaveEdit, noValidate: true }}
      >
        {/* noValidate: the row's save used to be a plain button, so native
            constraint validation never ran on these fields. */}
        <Stack spacing={2}>
          <TextField
            label={t("editNameLabel")}
            value={editName}
            slotProps={{ htmlInput: { maxLength: 50 } }}
            onChange={(e) => setEditName(e.target.value)}
          />
          <TextField
            label={t("sortLabel")}
            type="number"
            value={editSort}
            onChange={(e) => setEditSort(Number(e.target.value) || 0)}
            sx={{ maxWidth: "10rem" }}
          />
          <FormControlLabel
            label={t("saleableLabel")}
            control={<Checkbox checked={editSaleable} onChange={(e) => setEditSaleable(e.target.checked)} />}
          />
          <DialogError errors={errors} scope="edit" />
        </Stack>
      </Dialog>

      {/* Unconditional since #479: this slot is the page's alone now, so there
          is nothing a dialog's own message could double up with. */}
      {errors.page && <p className="error">{errors.page}</p>}

      <ListInspectorPane
          tableLabel={t("title")}
        table={(
          <LedgerTableContainer scrollHint="columnsAndRows">
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell sx={STICKY_TABLE_HEAD_SX}>{t("nameHeader")}</TableCell>
                  <TableCell sx={STICKY_TABLE_HEAD_SX}>{t("typeHeader")}</TableCell>
                  <TableCell align="right" sx={STICKY_TABLE_HEAD_SX}>{t("sortHeader")}</TableCell>
                  <TableCell sx={STICKY_TABLE_HEAD_SX}>{t("saleableHeader")}</TableCell>
                  <TableCell sx={STICKY_TABLE_HEAD_SX}>{t("statusHeader")}</TableCell>
                  <TableCell sx={STICKY_TABLE_HEAD_SX}>{tc("recordHistoryHeader")}</TableCell>
                  <TableCell sx={STICKY_TABLE_HEAD_SX}></TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {grades.map((g) => (
                  <TableRow key={g.id} className={g.active ? undefined : "inactive"}
                    {...selectableRowProps(g.id === selectedId, () => setSelectedId(g.id))}>
                    <TableCell sx={NOWRAP}>{g.name}</TableCell>
                    <TableCell sx={NOWRAP}>{gradeTypeLabel(g.gradeType)}</TableCell>
                    <TableCell align="right" sx={NOWRAP}>{fmt.count(g.sortOrder)}</TableCell>
                    <TableCell sx={NOWRAP}>{g.isSaleable ? <span className="badge badge-ok">{t("saleableYesBadge")}</span> : "—"}</TableCell>
                    <TableCell sx={NOWRAP}><StatusBadge status={g.active ? "Active" : "Inactive"} label={statusLabel(g.active ? "Active" : "Inactive")} /></TableCell>
                    <ProvenanceCell history={g} />
                    <TableCell sx={NOWRAP}>
                      <Stack direction="row" spacing={1} sx={{ flexWrap: "nowrap", alignItems: "center" }}>
                        {Object.entries(renderActions(g)).map(([key, action]) => <Fragment key={key}>{action}</Fragment>)}
                      </Stack>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </LedgerTableContainer>
        )}
        inspector={(
          <RecordInspector
            ariaLabel={tc("inspectorLabel", { entity: t("entitySingular") })}
            title={selectedGrade?.name}
            emptyMessage={tc("inspectorEmptyPrompt")}
            fields={selectedGrade ? [
              { label: t("typeHeader"), value: gradeTypeLabel(selectedGrade.gradeType) },
              { label: t("sortHeader"), value: fmt.count(selectedGrade.sortOrder) },
              { label: t("saleableHeader"), value: selectedGrade.isSaleable ? t("saleableYesBadge") : "—" },
              { label: t("statusHeader"), value: <StatusBadge status={selectedGrade.active ? "Active" : "Inactive"} label={statusLabel(selectedGrade.active ? "Active" : "Inactive")} /> },
            ] : undefined}
            actions={selectedGrade ? renderActions(selectedGrade) : undefined}
          />
        )}
      />
    </section>
  );
}
