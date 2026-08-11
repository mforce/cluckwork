import { useEffect, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Plus } from "lucide-react";
import {
  activateEggGrade, createEggGrade, deactivateEggGrade, listEggGrades, updateEggGrade,
} from "../api/cluckwork";
import type { EggGrade } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { DialogError } from "../components/DialogError";
import { ProvenanceCell } from "../components/ProvenanceCell";
import { StatusBadge } from "../components/StatusBadge";
import { useDialogErrors } from "../components/useDialogErrors";
import { usePendingAction } from "../components/usePendingAction";
import { newId } from "../lib/ids";
import i18n from "../i18n";
import { gradeTypeLabel, statusLabel } from "../i18n/enums";

const GRADE_TYPES = ["Size", "Quality", "Custom"];

function errorMessage(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// F6 (#42): manage the farm's egg grades. No hard delete — grade lines, lots,
// and order items reference grades forever; deactivation only removes a grade
// from capture/order pickers while history keeps rendering its name.
export function GradesPage() {
  const { t } = useTranslation("grades");
  const { t: tc } = useTranslation("common");
  // The grade catalog is configuration — management is admin-only (#73). The
  // nav link hides for workers; a direct URL just renders the list read-only.
  const { isAdmin } = useAuth();
  const [grades, setGrades] = useState<EggGrade[] | null>(null);
  // #479 — one slot per PLACE a message can appear: the initial load and the
  // row-level activate/deactivate writes (neither is behind a dialog) belong
  // to the page; create and edit each get their own dialog slot.
  const errors = useDialogErrors();
  const setPageError = errors.setPage;
  // #236: the flight guard + per-scope spinner state live in the shared hook;
  // this screen keeps only its idempotency-key and refresh discipline below.
  const { busy, isPending, run: runPending } = usePendingAction();

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

  // `dialogScope` names the DIALOG this attempt's failure belongs to — `null`
  // for the row-level activate/deactivate writes, which sit on the page.
  async function run(
    scope: string, dialogScope: string | null, action: (key: string) => Promise<unknown>,
  ): Promise<boolean> {
    const outcome = await runPending(scope, async () => {
      errors.beginAttempt(dialogScope);
      try {
        await action(keyFor(scope));
        // The refresh must succeed before the key rotates: if it throws, the key
        // survives and a retry replays the idempotent write instead of repeating it.
        setGrades(await fetchGrades());
        clearKey(scope);
        return true;
      } catch (err) {
        errors.report(dialogScope, errorMessage(err));
        return false;
      }
    });
    // A skipped run (another flight already open) reports `undefined` — never
    // success: mapping it to false keeps a blocked submit from closing its
    // dialog or resetting its form as if it had saved.
    return outcome ?? false;
  }

  // A dialog opens on a clean form; cancelling keeps whatever was typed until
  // the next open, so a stray Escape does not throw the entry away. Switching
  // straight from one dialog to the other, with no Cancel in between, abandons
  // the one being displaced, so its stale verdict cannot resurface next time it
  // reopens — the one case `abandon`-on-close alone cannot see. The backdrop
  // stops a mouse reaching the trigger underneath, so this is the rare path;
  // #480 established it does not stop a screen reader's virtual cursor, which
  // is the same reason a per-dialog map exists rather than one slot.
  function openCreate() {
    if (editingId !== null) errors.abandon(`edit:${editingId}`);
    setEditingId(null);
    setCreating(true);
  }

  // Dismissal empties the dialog's slot and mutes the attempt still out, so a
  // late failure is not reported against a session the user reopened.
  function closeCreate() {
    setCreating(false);
    errors.abandon("create");
  }

  function closeEdit() {
    const id = editingId;
    setEditingId(null);
    if (id !== null) errors.abandon(`edit:${id}`);
  }

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    const ok = await run("create-grade", "create", (key) =>
      createEggGrade({ name, gradeType, sortOrder, isSaleable }, key));
    if (ok) {
      setName("");
      setSortOrder(0);
      setIsSaleable(true);
      setCreating(false);
    }
  }

  function startEdit(g: EggGrade) {
    if (creating) errors.abandon("create");
    setCreating(false);
    // A different grade's edit DISPLACES this one the same way (see openCreate
    // above): the session ends without onClose, and its per-id slot would
    // otherwise replay the dead session's failure when THAT grade's edit is
    // reopened later (pi review of #491).
    if (editingId !== null && editingId !== g.id) errors.abandon(`edit:${editingId}`);
    setEditingId(g.id);
    setEditName(g.name);
    setEditSort(g.sortOrder);
    setEditSaleable(g.isSaleable);
  }

  async function onSaveEdit(e: FormEvent) {
    e.preventDefault();
    const id = editingId;
    if (id === null) return;
    const ok = await run(`update:${id}`, `edit:${id}`, (key) =>
      updateEggGrade(id, { name: editName, sortOrder: editSort, isSaleable: editSaleable }, key));
    if (ok) setEditingId(null);
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
      <Dialog open={creating && isAdmin} title={t("newGradeDialogTitle")} onClose={closeCreate}>
        <form className="inline-form" onSubmit={onCreate}>
          <label>{t("nameLabel")}
            <input value={name} required maxLength={50}
              onChange={(e) => setName(e.target.value)} />
          </label>
          <label>{t("typeLabel")}
            <select value={gradeType} onChange={(e) => setGradeType(e.target.value)}>
              {GRADE_TYPES.map((gt) => <option key={gt} value={gt}>{gradeTypeLabel(gt)}</option>)}
            </select>
          </label>
          <label>{t("sortLabel")}
            <input className="cell" type="number" value={sortOrder}
              onChange={(e) => setSortOrder(e.target.valueAsNumber || 0)} />
          </label>
          <label className="muted check">
            <input type="checkbox" checked={isSaleable}
              onChange={(e) => setIsSaleable(e.target.checked)} />
            {t("saleableLabel")}
          </label>
          <DialogError errors={errors} scope="create" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeCreate}>{tc("cancel")}</button>
            <BusyButton type="submit" busy={isPending("create-grade")} disabled={busy}>{t("addGradeButton")}</BusyButton>
          </div>
        </form>
      </Dialog>

      <Dialog open={editingId !== null && isAdmin} title={t("editGradeDialogTitle")} onClose={closeEdit}>
        {/* noValidate: the row's save used to be a plain button, so native
            constraint validation never ran on these fields. */}
        <form className="inline-form" noValidate onSubmit={onSaveEdit}>
          <label>{t("editNameLabel")}
            <input value={editName} maxLength={50}
              onChange={(e) => setEditName(e.target.value)} />
          </label>
          <label>{t("sortLabel")}
            <input className="cell" type="number" value={editSort}
              onChange={(e) => setEditSort(e.target.valueAsNumber || 0)} />
          </label>
          <label className="muted check">
            <input type="checkbox" checked={editSaleable}
              onChange={(e) => setEditSaleable(e.target.checked)} />
            {t("saleableLabel")}
          </label>
          <DialogError errors={errors} scope={`edit:${editingId}`} />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeEdit}>{tc("cancel")}</button>
            <BusyButton type="submit" busy={editingId !== null && isPending(`update:${editingId}`)} disabled={busy}>
              {tc("save")}
            </BusyButton>
          </div>
        </form>
      </Dialog>

      {/* Unconditional since #479: this slot is the page's alone now, so there
          is nothing a dialog's own message could double up with. */}
      {errors.page && <p className="error">{errors.page}</p>}

      <table className="data">
        <thead>
          <tr>
            <th>{t("nameHeader")}</th>
            <th>{t("typeHeader")}</th>
            <th>{t("sortHeader")}</th>
            <th>{t("saleableHeader")}</th>
            <th>{t("statusHeader")}</th>
            <th>{tc("recordHistoryHeader")}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {grades.map((g) => (
            <tr key={g.id} className={g.active ? undefined : "inactive"}>
              <td>{g.name}</td>
              <td>{gradeTypeLabel(g.gradeType)}</td>
              <td>{g.sortOrder}</td>
              <td>{g.isSaleable ? <span className="badge badge-ok">{t("saleableYesBadge")}</span> : "—"}</td>
              <td><StatusBadge status={g.active ? "Active" : "Inactive"} label={statusLabel(g.active ? "Active" : "Inactive")} /></td>
              <ProvenanceCell history={g} />
              <td>
                {isAdmin && (
                  <>
                    {/* Opens the edit dialog — non-mutating, so the spinner
                        belongs to the dialog's Save, not here (#242). */}
                    <button className="link" disabled={busy}
                      onClick={() => startEdit(g)}>{t("editButton")}</button>
                    {g.active ? (
                      <BusyButton className="link" busy={isPending(`deactivate:${g.id}`)} disabled={busy}
                        onClick={() => void run(`deactivate:${g.id}`, null, (key) => deactivateEggGrade(g.id, key))}>
                        {t("deactivateButton")}
                      </BusyButton>
                    ) : (
                      <BusyButton className="link" busy={isPending(`activate:${g.id}`)} disabled={busy}
                        onClick={() => void run(`activate:${g.id}`, null, (key) => activateEggGrade(g.id, key))}>
                        {t("activateButton")}
                      </BusyButton>
                    )}
                  </>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
