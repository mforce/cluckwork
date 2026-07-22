import { useEffect, useMemo, useRef, useState } from "react";
import type { FormEvent } from "react";
import {
  createFlock, listDailyEntries, listEggGrades, listFlocks,
  recordDailyEntry, submitDailyEntry,
} from "../api/cluckwork";
import type { EggGrade, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { Dialog } from "../components/Dialog";
import { useConfirm } from "../components/useConfirm";
import { todayIso } from "../lib/dates";

const LAST_FLOCK_KEY = "cluckwork.lastFlockId";

// Capture targets active flocks plus depleted ones — a depleted flock still
// accepts backfilled entries up to its depletion date (the API gates exact
// dates), matching the Flocks screen's promise and the feed-usage picker.
// Archived flocks accept nothing and stay hidden. Every flock refresh on this
// page (initial load AND the new-flock dialog) must go through this filter.
const capturable = (flocks: Flock[]) => flocks.filter((x) => x.status !== "Archived");

function errorMessage(err: unknown): string {
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// F1 (#21): record the day's production by grade, then submit — submitting
// turns grade lines into egg lots (stock).
export function DailyEntryPage() {
  const [loading, setLoading] = useState(true);
  const { confirm, confirmDialog } = useConfirm();
  const [flocks, setFlocks] = useState<Flock[]>([]);
  const [grades, setGrades] = useState<EggGrade[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [flockId, setFlockId] = useState("");
  const [date, setDate] = useState(todayIso());
  const [totalEggs, setTotalEggs] = useState(0);
  const [cracked, setCracked] = useState(0);
  const [dirty, setDirty] = useState(0);
  const [discarded, setDiscarded] = useState(0);
  const [mortality, setMortality] = useState(0);
  const [gradeQty, setGradeQty] = useState<Record<string, number>>({});
  // Grades are only sent when the user (or prefill) touched them: the server
  // treats [] as "explicitly clear all lines" and omitted as "leave unchanged",
  // so an untouched re-save must not wipe an existing entry's grading.
  const [gradesTouched, setGradesTouched] = useState(false);
  const [existingStatus, setExistingStatus] = useState<string | null>(null);
  // Prefill failure OR in-flight prefill blocks saving (silent-overwrite
  // guard, #59); failedTarget marks which flock+date the failure was for.
  const [prefillFailed, setPrefillFailed] = useState(false);
  const [prefillPending, setPrefillPending] = useState(false);
  const [prefillRetry, setPrefillRetry] = useState(0);
  const failedTarget = useRef<string | null>(null);

  const [busy, setBusy] = useState(false);
  const inFlight = useRef(false);
  // Stable idempotency keys per logical mutation: regenerated only after a
  // definitive success, so a retry after an ambiguous network failure dedupes
  // server-side instead of repeating the write.
  const saveKey = useRef<string>(crypto.randomUUID());
  const flockKey = useRef<string>(crypto.randomUUID());
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  // inline flock creation
  const [showNewFlock, setShowNewFlock] = useState(false);
  const [newFlockName, setNewFlockName] = useState("");
  const [newFlockBreed, setNewFlockBreed] = useState("");
  const [newFlockPlaced, setNewFlockPlaced] = useState(todayIso());
  const [newFlockCount, setNewFlockCount] = useState(100);

  useEffect(() => {
    // includeInactive: an existing draft may reference a since-deactivated
    // grade; that line must render and survive a re-save (only the ACTIVE
    // saleable grades are offered for new input — see visibleGrades).
    Promise.all([listFlocks(), listEggGrades({ includeInactive: true })])
      .then(([all, g]) => {
        const f = capturable(all);
        setFlocks(f);
        setGrades(g.filter((x) => x.isSaleable));
        // Deep link from History's Draft "edit" (#85): ?flockId=…&date=….
        // The pair is applied atomically — applying only the date against a
        // fallback flock would open a DIFFERENT flock's day under the linked
        // date (codex review of #86). Read once on mount — route navigation
        // here always remounts the page.
        const params = new URLSearchParams(window.location.search);
        const wantedFlock = params.get("flockId");
        const wantedDate = params.get("date");
        // A real past-or-today calendar date — the regex alone admits
        // impossibilities like 2026-13-01 that would fail the prefill fetch
        // and block saving.
        const dateOk = wantedDate !== null
          && /^\d{4}-\d{2}-\d{2}$/.test(wantedDate)
          && !Number.isNaN(Date.parse(`${wantedDate}T00:00:00Z`))
          && new Date(`${wantedDate}T00:00:00Z`).toISOString().slice(0, 10) === wantedDate
          && wantedDate <= todayIso();
        const flockOk = wantedFlock !== null && f.some((x) => x.id === wantedFlock);
        const deepLinked = flockOk && dateOk;
        if (deepLinked) setDate(wantedDate!);
        else if (wantedFlock || wantedDate)
          setError("This edit link points at a flock or date that is no longer available — using the usual defaults instead.");
        const remembered = localStorage.getItem(LAST_FLOCK_KEY);
        // Default prefers an ACTIVE flock — depleted ones are backfill targets
        // you pick deliberately, not a default.
        const firstActive = f.find((x) => x.status === "Active") ?? f[0];
        if (deepLinked) setFlockId(wantedFlock!);
        else if (remembered && f.some((x) => x.id === remembered)) setFlockId(remembered);
        else if (firstActive) setFlockId(firstActive.id);
      })
      .catch(() => setLoadError("Could not load flocks/grades. Is the API up?"))
      .finally(() => setLoading(false));
  }, []);

  // Edit-awareness: when flock+date match an existing entry, prefill the form
  // so a re-save updates what's really there instead of clobbering it.
  useEffect(() => {
    if (!flockId || !date) return;
    let cancelled = false;
    // Saves stay blocked from the moment a prefill is in flight until it
    // SUCCEEDS — clearing the flag optimistically would reopen the exact
    // overwrite window this guard closes (#61 review).
    setPrefillPending(true);
    const target = `${flockId}|${date}`;
    // Voided entries vacate their day (#82): they must not prefill the form or
    // block a fresh save. limit > 1 because a re-recorded day keeps its voided
    // siblings on the same date and ordering between them is by id; 100 (the
    // server's default page) so the live row can't fall off the fetched set.
    listDailyEntries({ flockId, from: date, to: date, limit: 100 })
      .then((entries) => {
        if (cancelled) return;
        const existing = entries.find((e) => e.date === date && e.status !== "Voided");
        // A retry that recovers for the SAME flock+date must not zero the form:
        // the user may have typed while the banner was up, and with no server
        // entry there is nothing to overwrite.
        const isRetryRecovery = failedTarget.current === target;
        if (existing) {
          setTotalEggs(existing.totalEggs);
          setCracked(existing.crackedEggs);
          setDirty(existing.dirtyEggs);
          setDiscarded(existing.discardedEggs);
          setMortality(existing.mortalityCount);
          setGradeQty(Object.fromEntries(existing.grades.map((g) => [g.eggGradeId, g.quantity])));
          setGradesTouched(existing.grades.length > 0);
          setExistingStatus(existing.status);
        } else if (!isRetryRecovery) {
          setTotalEggs(0); setCracked(0); setDirty(0); setDiscarded(0); setMortality(0);
          setGradeQty({});
          setGradesTouched(false);
          setExistingStatus(null);
        }
        failedTarget.current = null;
        setPrefillFailed(false);
        setPrefillPending(false);
      })
      .catch(() => {
        // Not best-effort (#59): without the prefill we can't know whether this
        // day already has data — saving would overwrite it with zeros. Block
        // saving until a retry succeeds.
        if (!cancelled) {
          failedTarget.current = target;
          setPrefillFailed(true);
          setPrefillPending(false);
        }
      });
    return () => { cancelled = true; };
  }, [flockId, date, prefillRetry]);

  useEffect(() => {
    if (flockId) localStorage.setItem(LAST_FLOCK_KEY, flockId);
  }, [flockId]);

  const gradesSum = useMemo(
    () => Object.values(gradeQty).reduce((a, b) => a + (b || 0), 0),
    [gradeQty],
  );
  // Active grades take input; a deactivated grade appears only while a
  // prefilled draft still carries a quantity for it — hiding it would
  // silently drop that line on the next save (the server's ReplaceGrades
  // removes omitted grades). Found by the PR #74 accuracy review.
  const visibleGrades = useMemo(
    () => grades.filter((g) => g.active || (gradeQty[g.id] ?? 0) > 0),
    [grades, gradeQty],
  );
  const losses = cracked + dirty + discarded;
  const sellable = totalEggs - losses;
  const lossesExceedTotal = losses > totalEggs;
  const selectedFlock = flocks.find((f) => f.id === flockId);
  const entryLocked = existingStatus !== null && existingStatus !== "Draft";

  const clamp0 = (v: number) => Math.max(0, v || 0);

  async function onCreateFlock(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      const created = await createFlock({
        name: newFlockName,
        breed: newFlockBreed,
        placementDate: newFlockPlaced,
        initialCount: newFlockCount,
      }, flockKey.current);
      flockKey.current = crypto.randomUUID();
      const refreshed = capturable(await listFlocks());
      setFlocks(refreshed);
      setFlockId(created.id);
      setShowNewFlock(false);
      setNewFlockName("");
      setNewFlockBreed("");
      setNewFlockPlaced(todayIso());
      setNewFlockCount(100);
    } catch (err) {
      setError(errorMessage(err));
    }
  }

  async function onSave(submit: boolean) {
    if (inFlight.current || !selectedFlock || prefillFailed || prefillPending) return;   // sync re-entry guard
    // One-way action (#59): submit freezes the day and creates egg lots.
    if (submit) {
      const ok = await confirm({
        title: "Submit this day?",
        body: "Egg lots are created and the entry can no longer be edited. "
          + "Corrections after this need a manager adjustment.",
        confirmLabel: "Submit day",
      });
      if (!ok) return;
      // The guard above ran before the await. window.confirm blocked the thread
      // so nothing could slip through; the dialog does not, so re-check it —
      // the state it guards may have moved while the question was on screen.
      if (inFlight.current || !selectedFlock || prefillFailed || prefillPending) return;
    }
    inFlight.current = true;
    setBusy(true);
    setError(null);
    setMessage(null);
    try {
      const lines = visibleGrades
        .filter((g) => (gradeQty[g.id] ?? 0) > 0)
        .map((g) => ({ eggGradeId: g.id, quantity: gradeQty[g.id] }));
      const created = await recordDailyEntry({
        farmId: selectedFlock.farmId,
        houseId: selectedFlock.houseId,
        flockId: selectedFlock.id,
        date,
        totalEggs,
        crackedEggs: cracked,
        dirtyEggs: dirty,
        discardedEggs: discarded,
        mortalityCount: mortality,
        grades: gradesTouched ? lines : undefined,
      }, saveKey.current);
      if (submit) {
        const result = await submitDailyEntry(created.id, saveKey.current);
        setExistingStatus(result.status);
        setMessage(`Submitted — ${result.eggLotIds.length} egg lot(s) created.`);
      } else {
        setMessage("Draft saved.");
      }
      saveKey.current = crypto.randomUUID();
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      inFlight.current = false;
      setBusy(false);
    }
  }

  if (loading) return <section><h2>Daily entry</h2><p className="muted">Loading…</p></section>;
  if (loadError) return <section><h2>Daily entry</h2><p className="error">{loadError}</p></section>;

  return (
    <section>
      <h2>Daily entry</h2>

      <div className="form-grid">
        <label>
          Flock
          <select value={flockId} onChange={(e) => setFlockId(e.target.value)}>
            {flocks.length === 0 && <option value="">— no flocks yet —</option>}
            {flocks.map((f) => (
              <option key={f.id} value={f.id}>
                {f.name} ({f.breed}){f.status === "Depleted" ? " — depleted, backfill only" : ""}
              </option>
            ))}
          </select>
        </label>
        <button className="link" type="button" onClick={() => { setError(null); setShowNewFlock(true); }}>
          + new flock
        </button>
      </div>

      {/* F131: creating a flock is catalog work, not capture — it belongs in a
          dialog like every other create, instead of shoving the entry grid
          down the page the moment the picker has nothing to offer yet. */}
      <Dialog open={showNewFlock} title="New flock" onClose={() => setShowNewFlock(false)}>
        <form className="inline-form" onSubmit={onCreateFlock}>
          <label>Name
            <input value={newFlockName} required
              onChange={(e) => setNewFlockName(e.target.value)} />
          </label>
          <label>Breed
            <input value={newFlockBreed} required
              onChange={(e) => setNewFlockBreed(e.target.value)} />
          </label>
          <label>Placed
            <input type="date" value={newFlockPlaced} max={todayIso()} required
              onChange={(e) => setNewFlockPlaced(e.target.value)} />
          </label>
          <label>Birds
            <input type="number" min={1} value={newFlockCount} required
              onChange={(e) => setNewFlockCount(Math.max(1, e.target.valueAsNumber || 1))} />
          </label>
          {/* The dialog carries its own copy while it is up. */}
      {error && !showNewFlock && <p className="error">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={() => setShowNewFlock(false)}>Cancel</button>
            <button type="submit">Create flock</button>
          </div>
        </form>
      </Dialog>

      <div className="form-grid">
        <label>Date
          <input type="date" value={date} max={todayIso()}
            onChange={(e) => setDate(e.target.value)} />
        </label>
        <label>Total eggs
          <input type="number" min={0} value={totalEggs} disabled={entryLocked}
            onChange={(e) => setTotalEggs(clamp0(e.target.valueAsNumber))} />
        </label>
        <label>Cracked
          <input type="number" min={0} value={cracked} disabled={entryLocked}
            onChange={(e) => setCracked(clamp0(e.target.valueAsNumber))} />
        </label>
        <label>Dirty
          <input type="number" min={0} value={dirty} disabled={entryLocked}
            onChange={(e) => setDirty(clamp0(e.target.valueAsNumber))} />
        </label>
        <label>Discarded
          <input type="number" min={0} value={discarded} disabled={entryLocked}
            onChange={(e) => setDiscarded(clamp0(e.target.valueAsNumber))} />
        </label>
        <label>Mortality
          <input type="number" min={0} value={mortality} disabled={entryLocked}
            onChange={(e) => setMortality(clamp0(e.target.valueAsNumber))} />
        </label>
      </div>

      {entryLocked && (
        <p className="warn">
          This day is already {existingStatus?.toLowerCase()} — its egg lots exist.
          Corrections are made from History (admins: adjust or void).
        </p>
      )}

      {prefillFailed && (
        <p className="error">
          Could not check whether this day already has an entry — saving is blocked
          so existing data isn't overwritten.{" "}
          <button className="link" type="button"
            onClick={() => setPrefillRetry((n) => n + 1)}>retry</button>
        </p>
      )}

      <h3>Sellable production by grade</h3>
      <div className="form-grid">
        {visibleGrades.map((g) => (
          <label key={g.id}>{g.name}{g.active ? "" : " (deactivated)"}
            <input type="number" min={0} value={gradeQty[g.id] ?? 0} disabled={entryLocked}
              onChange={(e) => {
                setGradesTouched(true);
                setGradeQty((prev) => ({ ...prev, [g.id]: clamp0(e.target.valueAsNumber) }));
              }} />
          </label>
        ))}
      </div>
      <p className={gradesSum > sellable || lossesExceedTotal ? "error" : "muted"}>
        {lossesExceedTotal
          ? `Cracked + dirty + discarded (${losses}) exceed total eggs (${totalEggs}).`
          : `Graded ${gradesSum} of ${sellable} sellable (total − cracked − dirty − discarded).`}
      </p>

      {/* The dialog carries its own copy while it is up. */}
      {error && !showNewFlock && <p className="error">{error}</p>}
      {message && <p className="success">{message}</p>}

      <div className="actions">
        <button disabled={busy || !flockId || lossesExceedTotal || entryLocked || prefillFailed || prefillPending}
          onClick={() => onSave(false)}>Save draft</button>
        <button
          disabled={busy || !flockId || lossesExceedTotal || gradesSum > sellable || entryLocked || prefillFailed || prefillPending}
          onClick={() => onSave(true)}>
          Save &amp; submit (creates egg lots)
        </button>
      </div>

      {confirmDialog}
    </section>
  );
}
