import { useEffect, useId, useMemo, useRef, useState } from "react";
import type { FormEvent } from "react";
import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import {
  createFlock, formatMoney, listDailyEntries, listEggGrades, listEggUnitConversions,
  listFeedUsage, listFlocks, listWaterUsage, recordDailyEntry, submitDailyEntry,
} from "../api/cluckwork";
import type { EggGrade, EggUnitConversion, FeedUsage, Flock, WaterUsage } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { StatusBadge } from "../components/StatusBadge";
import { GradingChip, TakeRemainderButton, remainderDropProps } from "../components/GradingChip";
import { NumberField } from "../components/NumberField";
import { useConfirm } from "../components/useConfirm";
import { usePendingAction } from "../components/usePendingAction";
import { useFarm, useFarmToday } from "../farm/useFarm";
import { armedState, gradingState } from "../lib/grading";
import { newId } from "../lib/ids";
import { resolveStepperUnit } from "../lib/stepperUnit";
import { useMe } from "../session/SessionContext";
import i18n from "../i18n";
import { statusLabel } from "../i18n/enums";

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
  const { t } = useTranslation("dailyEntry");
  const { t: tc } = useTranslation("common");
  // Farm-local, not browser-local: since #35 the API judges "is this date in
  // the future?" against the FARM's day, so the pickers must agree (#123).
  const today = useFarmToday();
  const [loading, setLoading] = useState(true);
  const { confirm, confirmDialog } = useConfirm();
  const [flocks, setFlocks] = useState<Flock[]>([]);
  const [grades, setGrades] = useState<EggGrade[]>([]);
  // #444 — the stepper's base increment resolves from farm default + user
  // override, both already fetched by the shell (useFarm/useMe); only the
  // conversion catalog is this screen's own read.
  const [eggUnitConversions, setEggUnitConversions] = useState<EggUnitConversion[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [flockId, setFlockId] = useState("");
  const [date, setDate] = useState(today);
  const [totalEggs, setTotalEggs] = useState(0);
  const [cracked, setCracked] = useState(0);
  const [dirty, setDirty] = useState(0);
  const [discarded, setDiscarded] = useState(0);
  const [mortality, setMortality] = useState(0);
  const [gradeQty, setGradeQty] = useState<Record<string, number>>({});
  // #443 — setGrade reads this instead of the `gradeQty` closure so a
  // hold-to-repeat burst (each tick its own onChange call, still against the
  // SAME setGrade closure captured at press-time — see NumberField's own
  // `live` ref for the identical problem) sees every earlier tick's write,
  // not just the value from the render the hold began on. Synced on every
  // render AND written immediately inside setGrade, matching NumberField's
  // pattern: the render-time write alone would still lag one tick behind
  // during a burst faster than a commit.
  const gradeQtyRef = useRef(gradeQty);
  gradeQtyRef.current = gradeQty;
  // Grades are only sent when the user (or prefill) touched them: the server
  // treats [] as "explicitly clear all lines" and omitted as "leave unchanged",
  // so an untouched re-save must not wipe an existing entry's grading.
  const [gradesTouched, setGradesTouched] = useState(false);
  const [existingStatus, setExistingStatus] = useState<string | null>(null);
  // Prefill failure OR in-flight prefill blocks saving (silent-overwrite
  // guard, #59); failedTarget marks which flock+date the failure was for.
  const [prefillFailed, setPrefillFailed] = useState(false);
  const [prefillPending, setPrefillPending] = useState(false);
  // #446 — the day-support strip: what else was recorded for this flock+date.
  // DELIBERATELY isolated from the prefill state machine above it (its own
  // effect, its own state, no retry) — a failed summary read hides the strip
  // and must never gate or zero the entry form. Queried by flock+date, not by
  // DailyEntryId: the strip works before the day's entry exists, which is
  // exactly when a farmer is most likely filling this screen in.
  const [daySupport, setDaySupport] =
    useState<{ feed: FeedUsage[]; water: WaterUsage[] } | null>(null);
  const [prefillRetry, setPrefillRetry] = useState(0);
  const failedTarget = useRef<string | null>(null);

  // One shared flight for save/submit/create-flock (#236): the hook's internal
  // ref replaced the hand-rolled inFlight ref this screen used to carry.
  const { busy, isPending, run } = usePendingAction();
  // Stable idempotency keys per logical mutation: regenerated only after a
  // definitive success, so a retry after an ambiguous network failure dedupes
  // server-side instead of repeating the write.
  const saveKey = useRef<string>(newId());
  const flockKey = useRef<string>(newId());
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  // inline flock creation
  const [showNewFlock, setShowNewFlock] = useState(false);
  const [newFlockName, setNewFlockName] = useState("");
  const [newFlockBreed, setNewFlockBreed] = useState("");
  const [newFlockPlaced, setNewFlockPlaced] = useState(today);
  const [newFlockCount, setNewFlockCount] = useState(100);

  useEffect(() => {
    // includeInactive: an existing draft may reference a since-deactivated
    // grade; that line must render and survive a re-save (only the ACTIVE
    // saleable grades are offered for new input — see visibleGrades).
    Promise.all([listFlocks(), listEggGrades({ includeInactive: true }), listEggUnitConversions()])
      .then(([all, g, units]) => {
        const f = capturable(all);
        setFlocks(f);
        // #396 — saleable AND hand-graded. Cracked and Dirty are saleable now,
        // so filtering on isSaleable alone would put them in the Grading pane;
        // their counters already produce a lot, and a manual line naming one
        // would produce a second lot for the same grade on the same day. The
        // server refuses that outright (ConditionGradeGuard) — this keeps the
        // screen from offering a control whose only outcome is a rejection.
        setGrades(g.filter((x) => x.isSaleable && x.dailyEntryKind === "Manual"));
        setEggUnitConversions(units);
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
          && wantedDate <= today;
        const flockOk = wantedFlock !== null && f.some((x) => x.id === wantedFlock);
        const deepLinked = flockOk && dateOk;
        if (deepLinked) retarget(() => setDate(wantedDate!));
        else if (wantedFlock || wantedDate)
          setError(i18n.t("dailyEntry:deepLinkUnavailable"));
        const remembered = localStorage.getItem(LAST_FLOCK_KEY);
        // Default prefers an ACTIVE flock — depleted ones are backfill targets
        // you pick deliberately, not a default.
        const firstActive = f.find((x) => x.status === "Active") ?? f[0];
        if (deepLinked) retarget(() => setFlockId(wantedFlock!));
        else if (remembered && f.some((x) => x.id === remembered)) retarget(() => setFlockId(remembered));
        else if (firstActive) retarget(() => setFlockId(firstActive.id));
      })
      .catch(() => setLoadError(i18n.t("dailyEntry:loadFlocksGradesFailed")))
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

  // #446 — see the daySupport state comment for why this effect is isolated.
  useEffect(() => {
    if (!flockId || !date) { setDaySupport(null); return; }
    let cancelled = false;
    setDaySupport(null);
    Promise.all([
      listFeedUsage({ flockId, from: date, to: date, limit: 100 }),
      listWaterUsage({ flockId, from: date, to: date, limit: 100 }),
    ])
      .then(([feed, water]) => { if (!cancelled) setDaySupport({ feed, water }); })
      .catch(() => { /* strip stays hidden; the entry form is untouched */ });
    return () => { cancelled = true; };
  }, [flockId, date]);

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
  // The day's arithmetic and its wording live in lib/grading (shared with
  // History's adjust dialog, which mirrors this layout) so the two screens can
  // never disagree about what "the day adds up" means (#394).
  const state = gradingState({ totalEggs, cracked, dirty, discarded, gradesSum });
  const { losses, sellable, lossesExceedTotal, remaining } = state;
  // #444 — the user's own preference wins over the farm default; both fall
  // back to "Individual" (today's plain +1/-1) if unset or unconfigured.
  const { farm } = useFarm();
  const me = useMe();
  const stepperUnit = resolveStepperUnit(
    farm?.defaultStepperUnit, me?.preferredStepperUnit, eggUnitConversions);
  const stepSize = stepperUnit.eggsPerUnit;
  const selectedFlock = flocks.find((f) => f.id === flockId);
  const entryLocked = existingStatus !== null && existingStatus !== "Draft";
  // The prefill found a draft for this flock+date: the form is EDITING it,
  // not starting fresh, and nothing said so before (#134).
  //
  // Gated on a SETTLED prefill. existingStatus still holds the previous
  // flock+date's value while a new one is in flight, and is never cleared if
  // that fetch fails — so without this the badge claims "editing draft" for a
  // day it knows nothing about, and keeps claiming it (codex review).
  const editingDraft = existingStatus === "Draft" && !prefillPending && !prefillFailed;
  // Grading counts DOWN to zero (see lib/grading). "Graded 12 of 407" made the
  // user do the subtraction; the number they are working towards is what is
  // left. Derived once and rendered twice: in full beside the grades, and
  // compressed in the pinned bar for phones, where both panes scroll away.
  // F134: dump the whole remainder into one grade. Most days are lopsided —
  // one grade takes the bulk — so the last step is "and the rest are Large".
  const [assigning, setAssigning] = useState(false);

  const grading = {
    tone: state.tone,
    count: state.count,
    says: t(state.saysKey),
    short: t(state.shortKey),
  };

  // Not while the prefill is unsettled: the remainder is computed from counts
  // that are about to be replaced, and handing those to a grade would assign
  // a figure belonging to the previous day.
  const canAssign = remaining > 0 && !entryLocked && !prefillPending && !prefillFailed;
  // DERIVED, not the raw flag (codex round 2 of #403, found on History's mirror
  // of this pane and applied here too): the day can reconcile — or lock, or
  // start a prefill — by something other than the gesture itself, and the
  // effect below only catches up on the NEXT render. Between the two, reading
  // `assigning` directly left rows armed with nothing to place, and on the
  // capture screen it also meant a "+0 here" button over a locked day.
  const armed = armedState(assigning, canAssign);
  // The effect still clears the stale flag, so becoming assignable again does
  // not silently re-arm rows the user is no longer aiming at.
  useEffect(() => {
    if (!canAssign && assigning) setAssigning(false);
  }, [canAssign, assigning]);

  // Changing the flock or the date starts a different day; staying armed over
  // the new one would be a held gesture the user never aimed at it.
  //
  // The pickers ALSO disarm synchronously (see `retarget` below), because this
  // effect — like the one above — only catches up on the next render, and the
  // guard that would have covered the gap (`prefillPending`) is itself set in
  // an effect. So for one render the rows were armed against the new day while
  // still holding the OLD day's remainder (codex round 3). This stays for the
  // paths that change the target without going through a picker.
  useEffect(() => setAssigning(false), [flockId, date]);

  // EVERY change of flock or date goes through here — the pickers, the
  // new-flock dialog, and the mount-time deep-link/default selection — so the
  // disarm lands in the same event as the change and no render can show one
  // day's remainder over another's. The mount path cannot be armed yet, and is
  // routed anyway so the rule is "no raw setFlockId/setDate, ever", which a
  // test can check and a reader cannot get wrong (#403 round 4).
  function retarget(apply: () => void) {
    setAssigning(false);
    apply();
  }

  // NumberField owns its own input, so the row label points at it by id.
  const fieldId = useId();
  const idFor = (name: string) => `${fieldId}-${name}`;

  // The counts are plain useState setters, so NumberField takes them as-is.
  // A grade lives in a record, so its updater is adapted here — still the
  // functional form, which the hold-to-repeat depends on.
  function assignRest(gradeId: string) {
    if (remaining <= 0 || entryLocked) return;
    setGrade(gradeId)((prev) => prev + remaining);
    setAssigning(false);
  }

  // #443 — grading may run ahead of the total (counted the grades before
  // adding them up); this used to be capped by NumberField's `max` instead.
  // Now a grade bump that would push the graded sum past what step 1's total
  // currently allows raises the total to match, computed here (not via a
  // `remaining`-watching effect) because an effect can't tell a total raised
  // BY this grade edit apart from `remaining` going negative because the
  // total itself was just lowered on step 1 — the latter must never be
  // fought back up.
  //
  // Gated on `newSum > prevSum` (codex review of #449) — not just "still over
  // the total" — for the same reason: after the user lowers the total below
  // an already-graded sum, correcting the grade back DOWN with − is also
  // "still over" on every step until it lands, and without this check each
  // decrement would ratchet the total back up toward the old sum, undoing
  // the very edit the user just made on step 1.
  const setGrade = (gradeId: string) => (next: number | ((prev: number) => number)) => {
    setGradesTouched(true);
    const current = gradeQtyRef.current;
    const prevSum = Object.values(current).reduce((a, b) => a + (b || 0), 0);
    const updated = {
      ...current,
      [gradeId]: typeof next === "function" ? next(current[gradeId] ?? 0) : next,
    };
    gradeQtyRef.current = updated;
    setGradeQty(updated);
    const newSum = Object.values(updated).reduce((a, b) => a + (b || 0), 0);
    if (newSum > prevSum) setTotalEggs((t) => Math.max(t, newSum + losses));
  };


  async function onCreateFlock(e: FormEvent) {
    e.preventDefault();
    // #236: this form shipped with NO in-flight guard at all — a double submit
    // reached the API twice. The hook's ref is the guard now.
    await run("create-flock", async () => {
      setError(null);
      try {
        const created = await createFlock({
          name: newFlockName,
          breed: newFlockBreed,
          placementDate: newFlockPlaced,
          initialCount: newFlockCount,
        }, flockKey.current);
        flockKey.current = newId();
        const refreshed = capturable(await listFlocks());
        setFlocks(refreshed);
        // Through `retarget` like the pickers: creating a flock switches the
        // captured day too, and nothing stops the dialog being opened while the
        // remainder gesture is armed (#403 round 4). Without it, the render
        // after the create shows the NEW flock's rows armed over the previous
        // flock's remainder — the picker bug reached by a different door.
        retarget(() => setFlockId(created.id));
        setShowNewFlock(false);
        setNewFlockName("");
        setNewFlockBreed("");
        setNewFlockPlaced(today);
        setNewFlockCount(100);
      } catch (err) {
        setError(errorMessage(err));
      }
    });
  }

  async function onSave(submit: boolean) {
    if (busy || !selectedFlock || prefillFailed || prefillPending) return;
    // One-way action (#59): submit freezes the day and creates egg lots.
    if (submit) {
      const ok = await confirm({
        title: i18n.t("dailyEntry:confirmSubmitTitle"),
        body: i18n.t("dailyEntry:confirmSubmitBody"),
        confirmLabel: i18n.t("dailyEntry:confirmSubmitLabel"),
      });
      if (!ok) return;
      // The dialog does not block the thread, so a double-click can land two
      // onSave calls before either opens a flight. (The second confirm settles
      // the first as dismissed, so the loser usually returns above.) The
      // ordering where it does not is caught by run()'s internal ref — two
      // survivors funnel into run and exactly one action starts.
      //
      // The state gates above are not re-read here: they would return the
      // render-time closure, and none of them can change while the dialog is
      // up. flock and date sit behind the backdrop, and a prefill cannot start
      // meanwhile because both save buttons are disabled while one is pending.
    }
    await run(submit ? "submit" : "save", async () => {
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
          setMessage(i18n.t("dailyEntry:submittedMessage", { count: result.eggLotIds.length }));
        } else {
          // The day now has saved work, so the badge should say so immediately.
          // Only the submit branch tracked status before, which left a first
          // draft save unbadged until a reload re-prefilled the same day.
          setExistingStatus("Draft");
          setMessage(i18n.t("dailyEntry:draftSavedMessage"));
        }
        saveKey.current = newId();
      } catch (err) {
        setError(errorMessage(err));
      }
    });
  }

  if (loading) return <section><h2>{t("title")}</h2><p className="muted">{tc("loading")}</p></section>;
  if (loadError) return <section><h2>{t("title")}</h2><p className="error">{loadError}</p></section>;

  return (
    <section>
      <div className="page-head">
        <h2>{t("title")}</h2>
        {/* Always rendered so it is a live region BEFORE the prefill fills it;
            a status container that appears at the same moment as its content is
            unreliably announced. */}
        <span role="status">
          {editingDraft && <StatusBadge status="Draft" label={t("editingDraftBadge")} />}
        </span>
      </div>

      {/* Context, not a step: choosing a flock and a date says WHICH day is
          being recorded, it is not part of recording it. The two steps below
          are the work, and they reconcile against each other. */}
      <div className="form-grid entry-context">
        <label>
          {t("flockLabel")}
          <select value={flockId} onChange={(e) => retarget(() => setFlockId(e.target.value))}>
            {flocks.length === 0 && <option value="">{t("noFlocksYetOption")}</option>}
            {flocks.map((f) => (
              <option key={f.id} value={f.id}>
                {f.name} ({f.breed}){f.status === "Depleted" ? t("depletedFlockSuffix") : ""}
              </option>
            ))}
          </select>
        </label>
        <label>{t("dateLabel")}
          <input type="date" value={date} max={today}
            onChange={(e) => retarget(() => setDate(e.target.value))} />
        </label>
        <button className="link" type="button" onClick={() => { setError(null); setShowNewFlock(true); }}>
          {t("newFlockButton")}
        </button>
      </div>

      {/* F131: creating a flock is catalog work, not capture — it belongs in a
          dialog like every other create, instead of shoving the entry grid
          down the page the moment the picker has nothing to offer yet. */}
      <Dialog open={showNewFlock} title={t("newFlockDialogTitle")} onClose={() => setShowNewFlock(false)}>
        <form className="inline-form" onSubmit={onCreateFlock}>
          <label>{t("nameLabel")}
            <input value={newFlockName} required
              onChange={(e) => setNewFlockName(e.target.value)} />
          </label>
          <label>{t("breedLabel")}
            <input value={newFlockBreed} required
              onChange={(e) => setNewFlockBreed(e.target.value)} />
          </label>
          <label>{t("placedLabel")}
            <input type="date" value={newFlockPlaced} max={today} required
              onChange={(e) => setNewFlockPlaced(e.target.value)} />
          </label>
          {/* #250: the one count on this page that was still a raw input.
              Sibling label, not wrapping — a <label> may not contain
              interactive content other than its own control, and the stepper
              carries two buttons. */}
          <div className="numfield-field">
            <label htmlFor={idFor("new-flock-birds")}>{t("birdsLabel")}</label>
            <NumberField id={idFor("new-flock-birds")} label={t("birdsLabel").toLowerCase()}
              value={newFlockCount} onChange={setNewFlockCount} min={1} />
          </div>
          {/* The dialog carries its own copy while it is up. This used to read
              `!showNewFlock` here, inside a dialog that only exists WHEN
              showNewFlock — so a failed create rendered no error anywhere and
              the button just appeared to do nothing (F134 review of #131). */}
          {error && <p className="error">{error}</p>}
          <div className="dialog-foot">
            <button type="button" className="link" onClick={() => setShowNewFlock(false)}>{tc("cancel")}</button>
            <BusyButton type="submit" busy={isPending("create-flock")} disabled={busy}>
              {t("createFlockButton")}
            </BusyButton>
          </div>
        </form>
      </Dialog>

      {entryLocked && (
        <p className="warn">
          {t("entryLockedBanner", { status: statusLabel(existingStatus ?? "").toLowerCase() })}
        </p>
      )}

      {prefillFailed && (
        <p className="error">
          {t("prefillFailedBanner")}{" "}
          <button className="link" type="button"
            onClick={() => setPrefillRetry((n) => n + 1)}>{tc("retry")}</button>
        </p>
      )}

      {/* #444 — reinforces what the "+30" on the buttons already says: which
          unit the taps count by and where that came from. Only when a pack
          unit is in force — "counting by ones" would be noise restating the
          default. */}
      {stepSize > 1 && (
        <p className="hint">
          {t("stepperUnitCaption", { unit: stepperUnit.unitCode, count: stepSize })}
        </p>
      )}

      {/* #446 — what else this flock's day already carries, with the way to
          the pages that record it. Joined on flock+date (never DailyEntryId),
          so it's live before the day's entry exists. */}
      {daySupport && (() => {
        // One currency per farm is the norm, but lot costs snapshot their
        // purchase-time currency — summing across a historical currency
        // change would blend units, so the cost drops rather than lies
        // (quality review of #446).
        const oneCurrency = daySupport.feed.every(
          (r) => r.currencyCode === daySupport.feed[0].currencyCode);
        const dayParams = `flockId=${flockId}&from=${date}&to=${date}`;
        return (
          <p className="hint">
            <Link className="link" to={`/feed?${dayParams}`}>
              {daySupport.feed.length === 0
                ? t("daySupportFeedNone")
                : oneCurrency
                  ? t("daySupportFeed", {
                      count: daySupport.feed.length,
                      cost: formatMoney(
                        daySupport.feed.reduce((a, r) => a + r.estimatedCostMinorUnits, 0),
                        daySupport.feed[0].currencyCode, daySupport.feed[0].currencyMinorUnit),
                    })
                  : t("daySupportFeedNoCost", { count: daySupport.feed.length })}
            </Link>
            {" · "}
            <Link className="link" to={`/water?${dayParams}`}>
              {daySupport.water.length > 0
                ? t("daySupportWater", { count: daySupport.water.length })
                : t("daySupportWaterNone")}
            </Link>
          </p>
        );
      })()}

      {/* Side by side, because the two panes reconcile: the sellable figure the
          left one produces is the target the right one has to hit. Reading one
          while the other was a screen away was the whole problem. */}
      <div className="entry-cols">
        <section className="entry-step">
          {/* The word boundaries live in the h3's own text nodes (ignored by the
              flex layout), not at the edges of the sr-only span: accessible-name
              computation trims each nested element's contribution, so edge
              whitespace inside the span is silently dropped. */}
          <h3><span className="step-n">{t("stepLabel", { n: 1 })}</span> <span className="sr-only">{t("stepOfTotal")}</span> {t("eggCountsHeading")}</h3>
          <div className="entry-pane">
            <div className="entry-rows">
              <div className="entry-row">
                <label htmlFor={idFor("total")}>{t("totalEggsLabel")}</label>
                <NumberField id={idFor("total")} label={t("totalEggsLabel").toLowerCase()}
                  value={totalEggs} onChange={setTotalEggs} step={stepSize} disabled={entryLocked} />
              </div>
              <div className="entry-row">
                <label htmlFor={idFor("cracked")}>{t("crackedLabel")}</label>
                <NumberField id={idFor("cracked")} label={t("crackedLabel").toLowerCase()}
                  value={cracked} onChange={setCracked} step={stepSize} disabled={entryLocked} />
              </div>
              <div className="entry-row">
                <label htmlFor={idFor("dirty")}>{t("dirtyLabel")}</label>
                <NumberField id={idFor("dirty")} label={t("dirtyLabel").toLowerCase()}
                  value={dirty} onChange={setDirty} step={stepSize} disabled={entryLocked} />
              </div>
              <div className="entry-row">
                <label htmlFor={idFor("discarded")}>{t("discardedLabel")}</label>
                <NumberField id={idFor("discarded")} label={t("discardedLabel").toLowerCase()}
                  value={discarded} onChange={setDiscarded} step={stepSize} disabled={entryLocked} />
              </div>
              <div className="entry-row">
                <label htmlFor={idFor("mortality")}>{t("mortalityLabel")}</label>
                {/* NO step: the pack unit counts EGGS. One tap here records a
                    dead BIRD, and submitting writes the bird-ledger movement —
                    a Tray farm must never log 30 deaths per tap (codex P1
                    review of #451). */}
                <NumberField id={idFor("mortality")} label={t("mortalityLabel").toLowerCase()}
                  value={mortality} onChange={setMortality} disabled={entryLocked} />
              </div>
            </div>

            {lossesExceedTotal ? (
              <p className="entry-readout error">
                {t("countsExceedTotalMessage", { losses, total: totalEggs })}
              </p>
            ) : (
              /* Shown as a value, not buried in a sentence — it is the target
                 the grading pane has to hit. */
              <p className="entry-readout">
                <span className="k">{t("sellableLabel")}<br />{t("sellableFormula", { total: totalEggs, cracked, dirty, discarded })}</span>
                <span className="v">{sellable}</span>
              </p>
            )}
          </div>
        </section>

        <section className="entry-step">
          <h3><span className="step-n">{t("stepLabel", { n: 2 })}</span> <span className="sr-only">{t("stepOfTotal")}</span> {t("gradingHeading")}</h3>
          <div className="entry-pane">
            <div className="entry-rows">
              {visibleGrades.map((g) => (
                <div
                  className={`entry-row${armed ? " taking" : ""}`}
                  key={g.id}
                  {...remainderDropProps(armed, () => assignRest(g.id))}
                >
                  <label htmlFor={idFor(g.id)}>{g.name}{g.active ? "" : t("deactivatedGradeSuffix")}</label>
                  {/* #443 — no max=: the old ceiling refused to let a grade
                      run ahead of step 1's total, forcing the total to be
                      known before grading could finish. setGrade now raises
                      the total to fit instead. */}
                  <NumberField id={idFor(g.id)} label={g.name.toLowerCase()}
                    value={gradeQty[g.id] ?? 0} onChange={setGrade(g.id)}
                    step={stepSize} disabled={entryLocked} />
                  {armed && (
                    <TakeRemainderButton remaining={remaining} grade={g.name}
                      onTake={() => assignRest(g.id)} />
                  )}
                </div>
              ))}
            </div>

            {/* The count changes as they type, and it is the only feedback that
                the day adds up — see GradingChip for its live-region shape. */}
            <GradingChip tone={grading.tone} count={grading.count} says={grading.says}
              canAssign={canAssign} remaining={remaining}
              assigning={armed} onAssigningChange={setAssigning} />
          </div>
        </section>
      </div>

      {/* Save feedback lives with the saves: anything below a pinned bar
          scrolls underneath it and is never read. */}
      <div className="entry-foot">
        {/* The dialog carries its own copy while it is up. */}
        {error && !showNewFlock && <p className="error">{error}</p>}
        {message && <p className="success">{message}</p>}
        <div className="entry-foot-row">
          {/* Phones only (see styles.css): the two panes stack there, so the
              figures that say whether the day adds up scroll away while the
              counts are being typed. On desktop both are already on screen and
              repeating them here would just be noise. */}
          <p className={`entry-foot-sum ${grading.tone}`} role="status">
            {/* `sellable` goes NEGATIVE once the losses pass the total, and this
                copy is phone-only — so the barn was the one place that got
                "-1 sellable" (review of PR #137). Pane 1 already branches to the
                explanation; say the same thing here rather than a broken sum. */}
            {lossesExceedTotal ? t("countsExceedFooterMessage") : (
              <>
                <b>{sellable}</b> {t("sellableWord")}
                {grading.count !== null && <> · <b>{grading.count}</b> {grading.short}</>}
              </>
            )}
          </p>
          <div className="actions">
            {/* Sibling triggers: each spins only for its own scope, while the
                shared `busy` in disabled keeps the other one inert.
                `grading.tone === "over"` (not the narrower `lossesExceedTotal`)
                because #443 made an over-graded draft reachable a second way:
                setGrade only ever RAISES the total to fit a grade, so the one
                path still left to "over" is trimming the total on step 1
                below a sum already graded — that must still block the save
                the lenient backend rule would reject anyway (#394), rather
                than round-trip to find out. `tone === "over"` already covers
                the lossesExceedTotal case too (see lib/grading). */}
            <BusyButton busy={isPending("save")}
              disabled={busy || !flockId || grading.tone === "over" || entryLocked || prefillFailed || prefillPending}
              onClick={() => onSave(false)}>{t("saveDraftButton")}</BusyButton>
            {/* #394: submit requires grading to reconcile EXACTLY — the same
                "done" state the chip and footer already show, so the gate can
                never say one thing and disable another. A draft may stay
                partially (or entirely un-)graded; only submit is gated. */}
            <BusyButton busy={isPending("submit")}
              disabled={busy || !flockId || grading.tone !== "done" || entryLocked || prefillFailed || prefillPending}
              onClick={() => onSave(true)}>
              {t("submitButton")}
            </BusyButton>
          </div>
        </div>
      </div>

      {confirmDialog}
    </section>
  );
}
