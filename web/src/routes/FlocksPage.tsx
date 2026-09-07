import { useCallback, useEffect, useId, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import { Bird, FilterX, Plus } from "lucide-react";
import {
  archiveFlock, createFlock, depleteFlock, listBirdMovements, listFlocks, reactivateFlock,
  recordBirdMovement, updateFlock,
} from "../api/cluckwork";
import type { BirdMovement, Flock } from "../api/cluckwork";
import { useFormat } from "../farm/useFormat";
import { FarmDate } from "../components/FarmDate";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { DialogError } from "../components/DialogError";
import { EmptyState } from "../components/EmptyState";
import { NumberField } from "../components/NumberField";
import { ProvenanceCell } from "../components/ProvenanceCell";
import { StatusBadge } from "../components/StatusBadge";
import { useConfirm } from "../components/useConfirm";
import { useDialogAction } from "../components/useDialogAction";
import { usePagedList } from "../components/usePagedList";
import { useAuth } from "../auth/useAuth";
import { ageWeeks } from "../lib/dates";
import { useFarmToday } from "../farm/useFarm";
import { newId } from "../lib/ids";
import i18n from "../i18n";
import { flockMovementLabel, statusLabel } from "../i18n/enums";

// The ledger's previous hard cap, kept as the page size: the endpoint's own
// max is 500, and 50 is what this screen has always shown at once.
const LEDGER_PAGE = 50;

// The scopes that own a dialog (#703). `run` routes a failure by this and gates
// a success by it; a scope outside the list — deplete/archive/reactivate from
// the row buttons — reports to the page and is never superseded.
const DIALOG_SCOPES = ["create", "edit", "record-movement"] as const;

// F7 (#47): manage flocks — create, correct identity fields, deplete, archive.
// Archived flocks leave pickers and the dashboard; this screen still shows them
// behind a toggle. Current bird count math is the mortality slice, not this one.
export function FlocksPage() {
  const { t } = useTranslation("flocks");
  const fmt = useFormat();
  const { t: tc } = useTranslation("common");
  // Farm-local, not browser-local: since #35 the API judges "is this date in
  // the future?" against the FARM's day, so the pickers must agree (#123).
  const today = useFarmToday();
  // Creating a flock, corrections, lifecycle changes, and manual movements
  // are all admin-only (#73, #388): a scoped Worker cannot assign the flock
  // it just created, so arrival is Owner/Manager administration too.
  const { isAdmin } = useAuth();
  const { confirm, confirmDialog } = useConfirm();
  const [flocks, setFlocks] = useState<Flock[] | null>(null);
  const [showArchived, setShowArchived] = useState(false);
  // #703 — the flight guard (#236), the per-place message slots (#479) and the
  // dialog-session generation (#477 part 2) come from one shared hook; this
  // screen keeps only its idempotency-key and refresh discipline below, and
  // says which scopes own a dialog.
  const { busy, isPending, errors, run, openDialog, dismissDialog } = useDialogAction(DIALOG_SCOPES);
  const setPageError = errors.setPage;

  // create form (F131: in a dialog)
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState("");
  const [breed, setBreed] = useState("");
  const [placed, setPlaced] = useState(today);
  const [count, setCount] = useState(100);
  // NumberField owns its own input, so labels point at it by id (#250).
  const fieldId = useId();
  const idFor = (name: string) => `${fieldId}-${name}`;

  // edit — dialog seeded from the row
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState("");
  const [editBreed, setEditBreed] = useState("");
  const [editPlaced, setEditPlaced] = useState("");
  const [editCount, setEditCount] = useState(0);

  // bird ledger (#54): one flock's movements open at a time
  const [ledgerFlockId, setLedgerFlockId] = useState<string | null>(null);
  const [mvDate, setMvDate] = useState(today);
  const [mvType, setMvType] = useState("Cull");
  const [mvQty, setMvQty] = useState(1);
  const [mvNote, setMvNote] = useState("");
  const [recording, setRecording] = useState(false); // F131: movement capture in a dialog

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

  const fetchFlocks = useCallback(
    () => listFlocks({ includeArchived: true, limit: 500 }),
    [],
  );

  useEffect(() => {
    fetchFlocks()
      .then(setFlocks)
      .catch(() => setPageError(i18n.t("flocks:loadFlocksFailed")));
  }, [fetchFlocks, setPageError]);

  // The write discipline every mutation on this screen shares: the write under
  // its idempotency key, the list refresh, and only THEN the key rotation — if
  // the refresh throws, the key survives and a retry replays the idempotent
  // write instead of duplicating it (grade-management review lesson). All
  // three are facts about the world: they run whether or not the dialog that
  // started them is still on screen (#703 — the superseded-safe rule). The
  // flight guard, the failure routing and the success gate are the hook's;
  // this helper is called INSIDE `run`, and anything it throws lands in the
  // slot `run` was given.
  async function commit(keyScope: string, action: (key: string) => Promise<unknown>): Promise<void> {
    await action(keyFor(keyScope));
    setFlocks(await fetchFlocks());
    clearKey(keyScope);
  }

  // F135: the two lifecycle changes ask first. Named handlers rather than the
  // inline row lambdas they replace, because the ask is now awaited.
  async function onDeplete(f: Flock) {
    const ok = await confirm({
      title: i18n.t("flocks:depleteConfirmTitle", { name: f.name }),
      body: i18n.t("flocks:depleteConfirmBody"),
      confirmLabel: i18n.t("flocks:depleteConfirmLabel"),
      destructive: true,
    });
    if (ok) await run(`deplete:${f.id}`, () => commit(`deplete:${f.id}`, (key) => depleteFlock(f.id, key)));
  }

  async function onArchive(f: Flock) {
    const ok = await confirm({
      title: i18n.t("flocks:archiveConfirmTitle", { name: f.name }),
      body: i18n.t("flocks:archiveConfirmBody"),
      confirmLabel: i18n.t("flocks:archiveConfirmLabel"),
      destructive: true,
    });
    if (ok) await run(`archive:${f.id}`, () => commit(`archive:${f.id}`, (key) => archiveFlock(f.id, key)));
  }

  // Dismissal is one of the two session edges (#703): it mutes the attempt
  // still out, so a late failure is not reported against a session the user
  // reopened, and ends the session, so a late success cannot act on it.
  const closeCreate = () => { setCreating(false); dismissDialog("create"); };

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    await run("create", async (current) => {
      await commit("create-flock", (key) =>
        createFlock({ name, breed, placementDate: placed, initialCount: count }, key));
      // Superseded: the flock exists and the list shows it, but the form and
      // its dialog belong to whatever session is on screen now (#703).
      if (!current()) return;
      setName("");
      setBreed("");
      setPlaced(today);
      setCount(100);
      setCreating(false);
    });
  }

  const closeEdit = () => { setEditingId(null); dismissDialog("edit"); };

  function startEdit(f: Flock) {
    closeCreate(); // defensive: New flock and Edit are mutually exclusive triggers
    // Opening is the other session edge (#703): whatever "edit" session was on
    // screen — this flock's or, reached behind the backdrop by a screen
    // reader's virtual cursor (#480; pi review of #491), another flock's — is
    // over, its attempt still out muted and its success unable to touch this
    // one. That is what used to need a displacement check here.
    openDialog("edit");
    setEditingId(f.id);
    setEditName(f.name);
    setEditBreed(f.breed);
    setEditPlaced(f.placementDate);
    setEditCount(f.initialCount);
  }

  async function onSaveEdit(e: FormEvent) {
    e.preventDefault();
    const id = editingId;
    if (id === null) return;
    // The run scope is the dialog's; the idempotency key stays per flock.
    await run("edit", async (current) => {
      await commit(`update:${id}`, (key) =>
        updateFlock(id, {
          name: editName, breed: editBreed,
          placementDate: editPlaced, initialCount: editCount,
        }, key));
      if (!current()) return;
      setEditingId(null);
    });
  }

  // #511 — the ledger is server-paged; the open flock IS the fetch identity, so
  // "the user switched flocks" and "reload from the top" are one event and
  // cannot drift apart. This replaces the hand-rolled ledgerRequest ref.
  const fetchMovements = useCallback(
    (offset: number, limit: number) =>
      ledgerFlockId
        ? listBirdMovements(ledgerFlockId, { limit, offset })
        : Promise.resolve<BirdMovement[]>([]),
    [ledgerFlockId],
  );
  const ledger = usePagedList<BirdMovement>({
    fetchPage: fetchMovements,
    pageSize: LEDGER_PAGE,
    errorText: () => i18n.t("flocks:loadMovementsFailed"),
  });

  const closeRecordMovement = () => { setRecording(false); dismissDialog("record-movement"); };

  function openLedger(id: string) {
    closeRecordMovement(); // a movement dialog belongs to the ledger that opened it
    if (ledgerFlockId === id) {
      setLedgerFlockId(null);
      return;
    }
    setLedgerFlockId(id);
    setMvDate(today);
  }

  async function onRecordMovement(e: FormEvent) {
    e.preventDefault();
    if (!ledgerFlockId) return;
    const id = ledgerFlockId;
    await run("record-movement", async (current) => {
      await commit(`movement:${id}`, async (key) => {
        await ledger.runWrite(async () => {
          await recordBirdMovement(id, {
            date: mvDate, type: mvType, quantity: mvQty,
            note: mvNote || undefined,
          }, key);
        });
      });
      if (!current()) return;
      setMvQty(1);
      setMvNote("");
      setRecording(false);
    });
  }

  if (errors.page && flocks === null) {
    return <section><h2>{t("title")}</h2><p className="error">{errors.page}</p></section>;
  }
  if (flocks === null) {
    return <section><h2>{t("title")}</h2><p className="muted">{tc("loading")}</p></section>;
  }

  const visible = flocks.filter((f) => showArchived || f.status !== "Archived");
  const archivedCount = flocks.filter((f) => f.status === "Archived").length;

  return (
    <section>
      <div className="page-head">
        <h2>{t("title")}</h2>
        {/* #655 — withheld exactly when the truly-empty state below is
            offering this same action (never for the filtered-empty branch,
            which offers "Clear filters" instead — no duplicate there). */}
        {isAdmin && !(flocks.length === 0) && (
          <button type="button" onClick={() => { closeEdit(); openDialog("create"); setCreating(true); }}>
            <Plus size={16} aria-hidden /> {t("newFlockButton")}
          </button>
        )}
      </div>
      <p className="muted">
        {t("intro")}
      </p>

      <Dialog open={creating && isAdmin} title={t("newFlockDialogTitle")} onClose={closeCreate}>
        <form className="inline-form" onSubmit={onCreate}>
          <label>{t("nameLabel")}
            <input value={name} required maxLength={100}
              onChange={(e) => setName(e.target.value)} />
          </label>
          <label>{t("breedLabel")}
            <input value={breed} required maxLength={100}
              onChange={(e) => setBreed(e.target.value)} />
          </label>
          <label>{t("placedLabel")}
            <input type="date" value={placed} max={today} required
              onChange={(e) => setPlaced(e.target.value)} />
          </label>
          {/* #250: sibling label, not wrapping — a <label> may not contain
              interactive content other than its own control, and the stepper
              carries two buttons. */}
          <div className="numfield-field">
            <label htmlFor={idFor("birds")}>{t("birdsLabel")}</label>
            <NumberField id={idFor("birds")} label={t("birdsLabel").toLowerCase()}
              value={count} onChange={setCount} min={1} />
          </div>
          <DialogError errors={errors} scope="create" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeCreate}>{tc("cancel")}</button>
            <BusyButton type="submit" busy={isPending("create")} disabled={busy}>{t("addFlockButton")}</BusyButton>
          </div>
        </form>
      </Dialog>

      {/* Editing is admin-only, so a role change mid-edit closes it. */}
      <Dialog open={editingId !== null && isAdmin} title={t("editFlockDialogTitle")} onClose={closeEdit}>
        {/* noValidate: the row's save used to be a plain button — native
            constraint validation never ran on these fields. */}
        <form className="inline-form" noValidate onSubmit={onSaveEdit}>
          <label>{t("editNameLabel")}
            <input value={editName} maxLength={100}
              onChange={(e) => setEditName(e.target.value)} />
          </label>
          <label>{t("editBreedLabel")}
            <input value={editBreed} maxLength={100}
              onChange={(e) => setEditBreed(e.target.value)} />
          </label>
          <label>{t("editPlacedLabel")}
            <input type="date" value={editPlaced} max={today}
              onChange={(e) => setEditPlaced(e.target.value)} />
          </label>
          <div className="numfield-field">
            <label htmlFor={idFor("edit-count")}>{t("editCountLabel")}</label>
            <NumberField id={idFor("edit-count")} label={t("editCountLabel").toLowerCase()}
              value={editCount} onChange={setEditCount} min={1} />
          </div>
          <DialogError errors={errors} scope="edit" />
          <div className="dialog-foot">
            <button type="button" className="link" onClick={closeEdit}>{tc("cancel")}</button>
            <BusyButton type="submit" busy={isPending("edit")} disabled={busy}>
              {tc("save")}
            </BusyButton>
          </div>
        </form>
      </Dialog>

      {/* #479 — unconditional: each dialog now renders its own failure through
          its own slot (DialogError above), so nothing here can be a stale copy
          of a dialog's message; this is only ever the page's own. */}
      {errors.page && <p className="error">{errors.page}</p>}

      {archivedCount > 0 && (
        <label className="muted check">
          <input type="checkbox" checked={showArchived}
            onChange={(e) => setShowArchived(e.target.checked)} />
          {t("showArchivedLabel", { count: archivedCount })}
        </label>
      )}

      {visible.length === 0 ? (
        // #655 — `visible` is already filtered by `showArchived`; when
        // everything is archived and the toggle is off, this is "filtered to
        // nothing" (offer to reveal them), never "nothing exists yet" (which
        // needs `archivedCount === 0` too — the toggle can't be responsible
        // for an empty `flocks` array in the first place).
        archivedCount > 0
          ? <EmptyState icon={FilterX} message={t("noFlocksMatch")}
              action={{ label: tc("clearFiltersButton"), onClick: () => setShowArchived(true) }} />
          : <EmptyState icon={Bird} message={t("noFlocksMessage")}
              action={isAdmin ? { label: t("newFlockButton"), onClick: () => { closeEdit(); openDialog("create"); setCreating(true); } } : undefined} />
      ) : (
        <table className="data">
          <thead>
            <tr>
              <th>{t("nameHeader")}</th><th>{t("breedHeader")}</th><th>{t("placedHeader")}</th><th className="num">{t("ageHeader")}</th>
              <th className="num">{t("birdsHeader")}</th><th>{t("statusHeader")}</th>
              <th>{tc("recordHistoryHeader")}</th><th></th>
            </tr>
          </thead>
          <tbody>
            {visible.map((f) => (
              <tr key={f.id} className={f.status === "Archived" ? "inactive" : undefined}>
                <td>{f.name}</td>
                <td>{f.breed}</td>
                <td className="nowrap"><FarmDate iso={f.placementDate} /></td>
                <td className="num">{t("ageWeeksSuffix", { weeks: ageWeeks(f.placementDate) })}</td>
                <td className="num">
                  {fmt.count(f.currentBirds)}
                  {f.currentBirds !== f.initialCount &&
                    <span className="muted"> / {fmt.count(f.initialCount)}</span>}
                </td>
                <td><StatusBadge status={f.status} label={statusLabel(f.status)} /></td>
                <ProvenanceCell history={f} />
                <td>
                  {/* #493 — full audit trail for this record, distinct from
                      the created/last-changed summary in ProvenanceCell.
                      Admin-gated: /api/v1/audit is AdminOnly, so a non-admin
                      following this link would only reach a 403 (codex
                      review of #516). */}
                  {isAdmin && (
                    <Link className="link" to={`/audit?entityId=${f.id}`}>
                      {tc("recordHistory.viewHistoryLink")}
                    </Link>
                  )}
                  <button className="link" disabled={busy}
                    onClick={() => void openLedger(f.id)}>
                    {ledgerFlockId === f.id ? t("closeLedgerButton") : t("openLedgerButton")}
                  </button>
                  {isAdmin && (
                    // Opens the edit dialog — non-mutating, so the spinner
                    // belongs to the dialog's Save, not here (#242).
                    <button className="link" disabled={busy}
                      onClick={() => startEdit(f)}>{t("editButton")}</button>
                  )}
                  {isAdmin && f.status === "Active" && (
                    <BusyButton className="link" busy={isPending(`deplete:${f.id}`)} disabled={busy}
                      onClick={() => void onDeplete(f)}>
                      {t("depleteButton")}
                    </BusyButton>
                  )}
                  {isAdmin && f.status !== "Archived" && (
                    // After the confirm dialog settles, THIS button is the
                    // pending indicator for the in-flight archive (#236).
                    <BusyButton className="link" busy={isPending(`archive:${f.id}`)} disabled={busy}
                      onClick={() => void onArchive(f)}>
                      {t("archiveButton")}
                    </BusyButton>
                  )}
                  {isAdmin && f.status !== "Active" && (
                    // The undo (#57): back to Active, full capture restored.
                    <BusyButton className="link" busy={isPending(`reactivate:${f.id}`)} disabled={busy}
                      onClick={() => void run(`reactivate:${f.id}`, () => commit(`reactivate:${f.id}`, (key) => reactivateFlock(f.id, key)))}>
                      {t("reactivateButton")}
                    </BusyButton>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {ledgerFlockId && (
        <div className="order-panel">
          <h3>
            {t("ledgerHeading", { name: flocks.find((f) => f.id === ledgerFlockId)?.name ?? "" })}
          </h3>
          <p className="muted">
            {t("ledgerIntro")}
            {isAdmin ? t("ledgerIntroAdminNote") : t("ledgerIntroWorkerNote")}
          </p>

          {isAdmin && (
            <button type="button" onClick={() => { openDialog("record-movement"); setRecording(true); }}>
              <Plus size={16} aria-hidden /> {t("recordMovementButton")}
            </button>
          )}

          <Dialog open={recording && isAdmin} title={t("recordMovementDialogTitle")} onClose={closeRecordMovement}>
            <form className="inline-form" onSubmit={onRecordMovement}>
              <label>{t("dateLabel")}
                <input type="date" value={mvDate} max={today}
                  onChange={(e) => setMvDate(e.target.value)} />
              </label>
              <label>{t("typeLabel")}
                <select value={mvType} onChange={(e) => setMvType(e.target.value)}>
                  <option value="Cull">{flockMovementLabel("Cull")}</option>
                  <option value="Adjustment">{flockMovementLabel("Adjustment")}</option>
                </select>
              </label>
              {/* A cull removes at least one bird; an Adjustment counts both
                  ways (added or lost), so its floor is unbounded (#250). */}
              <div className="numfield-field">
                <label htmlFor={idFor("mv-birds")}>{t("birdsLabel")}</label>
                <NumberField id={idFor("mv-birds")} label={t("birdsLabel").toLowerCase()}
                  value={mvQty} onChange={setMvQty}
                  min={mvType === "Cull" ? 1 : Number.NEGATIVE_INFINITY} />
              </div>
              <label>{t("noteLabel")}
                <input value={mvNote} maxLength={500}
                  onChange={(e) => setMvNote(e.target.value)} />
              </label>
              <DialogError errors={errors} scope="record-movement" />
              <div className="dialog-foot">
                <button type="button" className="link" onClick={closeRecordMovement}>{tc("cancel")}</button>
                <BusyButton type="submit"
                  busy={isPending("record-movement")}
                  disabled={busy || mvQty === 0}>
                  {t("recordButton")}
                </BusyButton>
              </div>
            </form>
          </Dialog>

          {/* #511 round 5 — the error renders BESIDE the rows, never instead of
              them. usePagedList keeps `rows` and `hasMore` when an EXTENSION
              fails (only a failed REPLACEMENT empties them), so a branch that
              swapped the table for the message threw away everything the user
              had paged to over one transient load-more failure. That is AC3:
              a failed extension keeps already-loaded rows and permits retry.
              CustomersPage had this right from the start — it is the shape
              copied here. A failed REPLACEMENT still shows the message alone,
              because the hook has emptied `rows` by then and the empty branch
              below does not fire on `error`. */}
          {ledger.error && <p className="error">{ledger.error}</p>}
          {ledger.rows === null || ledger.reloading ? (
            <p className="muted">{tc("loading")}</p>
          ) : ledger.rows.length === 0 && !ledger.error ? (
            <p className="muted">{t("noMovementsMessage")}</p>
          ) : (
            <table className="data">
              <thead>
                <tr><th>{t("ledgerDateHeader")}</th><th>{t("ledgerTypeHeader")}</th><th className="num">{t("ledgerBirdsHeader")}</th><th>{t("ledgerNoteHeader")}</th></tr>
              </thead>
              <tbody>
                {ledger.rows.map((m) => (
                  <tr key={m.id}>
                    <td className="nowrap"><FarmDate iso={m.date} /></td>
                    <td>{flockMovementLabel(m.type)}</td>
                    <td className="num">{m.quantity > 0 ? `−${fmt.count(m.quantity)}` : `+${fmt.count(-m.quantity)}`}</td>
                    <td>{m.note ?? "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {ledger.canLoadMore && (
            <button className="link" onClick={() => void ledger.loadMore()}>
              {t("loadMoreButton")}
            </button>
          )}
        </div>
      )}

      {confirmDialog}
    </section>
  );
}
