import { useCallback, useEffect, useId, useRef, useState } from "react";
import type { FormEvent } from "react";
import { useTranslation } from "react-i18next";
import { Link } from "react-router";
import {
  adjustDailyEntry, getDailyEntry, listDailyEntries, listEggGrades, listFlocks, voidDailyEntry,
} from "../api/cluckwork";
import type { DailyEntry, EggGrade, Flock } from "../api/cluckwork";
import { ApiError } from "../api/client";
import { useAuth } from "../auth/useAuth";
import { BusyButton } from "../components/BusyButton";
import { Dialog } from "../components/Dialog";
import { GradingChip, TakeRemainderButton, remainderDropProps } from "../components/GradingChip";
import { NumberField } from "../components/NumberField";
import { useConfirm } from "../components/useConfirm";
import { usePendingAction } from "../components/usePendingAction";
import { StatusBadge } from "../components/StatusBadge";
import { armedState, gradingState } from "../lib/grading";
import { newId } from "../lib/ids";
import i18n from "../i18n";

const PAGE = 50;

// Module-level helper — outside the hook's render context, so it always uses
// the imperative i18n.t() singleton (see CONTRIBUTING-i18n.md).
function errText(err: unknown): string {
  // Concurrent-correction conflicts get a human message instead of raw problem text.
  if (err instanceof ApiError && err.status === 409)
    return i18n.t("history:concurrentConflictMessage");
  if (err instanceof ApiError) return err.message;
  return err instanceof Error ? err.message : String(err);
}

// #24 (entries half): browse recorded daily entries, newest first, with
// flock + date-range filters and offset paging. #69 (part 2): admins can
// adjust or void submitted/locked entries from here — the API reconciles
// stock and the bird ledger and enforces the role either way.
export function HistoryPage() {
  const { t } = useTranslation("history");
  const { t: tc } = useTranslation("common");
  // The adjust dialog IS the Daily entry form (same two steps, same
  // reconciliation), so it speaks that screen's copy rather than a second
  // near-duplicate set of count labels and chip wording in this namespace.
  const { t: te } = useTranslation("dailyEntry");
  const { isAdmin } = useAuth();
  const { askReason, confirmDialog } = useConfirm();
  const [entries, setEntries] = useState<DailyEntry[] | null>(null);
  const [hasMore, setHasMore] = useState(false);
  const [flocks, setFlocks] = useState<Flock[]>([]);
  const [grades, setGrades] = useState<EggGrade[]>([]);
  const [flockFilter, setFlockFilter] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  // Per-row scopes (adjust:<id> / void:<id>): exactly one control spins while
  // `busy` keeps every other mutating control on the screen inert.
  const { busy, isPending, run } = usePendingAction();

  // adjust panel: one entry at a time; the version it was loaded with rides
  // along so a concurrent correction surfaces as a 409, not an overwrite.
  const [adjusting, setAdjusting] = useState<DailyEntry | null>(null);
  const [total, setTotal] = useState(0);
  // NumberField owns its own input, so the labels point at it by id (#250,
  // same F134 idiom as the daily-entry screen this form mirrors).
  const fieldId = useId();
  const idFor = (name: string) => `${fieldId}-${name}`;
  const [cracked, setCracked] = useState(0);
  const [dirty, setDirty] = useState(0);
  const [discarded, setDiscarded] = useState(0);
  const [mortality, setMortality] = useState(0);
  const [reason, setReason] = useState("");
  const [lineQty, setLineQty] = useState<Record<string, number>>({});
  // #443 — see DailyEntryPage's identical `gradeQtyRef` comment: setLine reads
  // this instead of the `lineQty` closure so a hold-to-repeat burst (each
  // tick against the SAME setLine closure captured at press-time) sees every
  // earlier tick's write.
  const lineQtyRef = useRef(lineQty);
  lineQtyRef.current = lineQty;
  // F134's remainder gesture, mirrored here: hand everything still unaccounted
  // for to one grade. A recount is lopsided the same way a capture is.
  const [assigning, setAssigning] = useState(false);

  // F131: the correction form is a dialog — it takes focus itself, so the
  // scroll-and-focus dance the old above-the-table panel needed is gone
  // (codex, PR #81).

  // Stable idempotency keys per logical mutation; see settleKey for the
  // rotation rules on this screen.
  const keys = useRef(new Map<string, string>());
  const keyFor = (scope: string) => {
    const existing = keys.current.get(scope);
    if (existing) return existing;
    const fresh = newId();
    keys.current.set(scope, fresh);
    return fresh;
  };
  const clearKey = (scope: string) => keys.current.delete(scope);

  useEffect(() => {
    // includeInactive/includeArchived: historical entries may reference
    // deactivated grades or archived flocks and their names must still resolve.
    Promise.all([listFlocks({ includeArchived: true }), listEggGrades({ includeInactive: true })])
      .then(([f, g]) => { setFlocks(f); setGrades(g); })
      .catch(() => setError(i18n.t("history:loadFlocksGradesFailed")));
  }, []);

  const load = useCallback(async (offset = 0) => {
    const page = await listDailyEntries({
      flockId: flockFilter || undefined,
      from: from || undefined,
      to: to || undefined,
      limit: PAGE,
      offset,
    });
    setHasMore(page.length === PAGE);
    setEntries((prev) => (offset === 0 ? page : [...(prev ?? []), ...page]));
  }, [flockFilter, from, to]);

  useEffect(() => {
    load().catch(() => setError(i18n.t("history:loadEntriesFailed")));
  }, [load]);

  const flockName = (id: string) => flocks.find((f) => f.id === id)?.name ?? id.slice(0, 8);
  // The Daily entry screen can't target archived flocks (capture excludes
  // them), so an edit link for one would silently fall back to a different
  // flock — worse than no link (codex review of #86).
  const flockEditable = (id: string) => {
    const f = flocks.find((x) => x.id === id);
    return f !== undefined && f.status !== "Archived";
  };
  const gradeName = (id: string) => grades.find((g) => g.id === id)?.name ?? id.slice(0, 8);
  const correctable = (e: DailyEntry) =>
    e.status === "Submitted" || e.status === "Locked" || e.status === "ManagerAdjusted";

  // #396 — how many of this day's cracked/dirty eggs became stock. Read from
  // the ENTRY's snapshot, never from the current grade catalog: a farm that
  // switched Cracked off after recording a day must still see that day as it
  // was recorded, and one that switched it on must not see past losses
  // retroactively turn into stock.
  //
  // A draft has resolved nothing yet, so it shows an em dash rather than 0 —
  // "not decided" and "decided to be a loss" are different facts, and 0 would
  // state the second while the first is true.
  const conditionStock = (e: DailyEntry) => {
    if (e.status === "Draft") return "—";
    return (e.crackedGradeId ? e.crackedEggs : 0) + (e.dirtyGradeId ? e.dirtyEggs : 0);
  };

  function startAdjust(e: DailyEntry) {
    setAdjusting(e);
    setTotal(e.totalEggs);
    setCracked(e.crackedEggs);
    setDirty(e.dirtyEggs);
    setDiscarded(e.discardedEggs);
    setMortality(e.mortalityCount);
    setReason("");
    setLineQty(Object.fromEntries(e.grades.map((g) => [g.eggGradeId, g.quantity])));
    // A different entry (or the server's newer copy after a 409) is a different
    // day: staying armed over it would be a gesture the user never aimed there.
    setAssigning(false);
  }

  // The entry's own lines (possibly deactivated grades — still correctable)
  // plus the active saleable catalog for adding a missed grade.
  //
  // #396 — a counter-fed grade is never offered for adding. It is excluded from
  // the CATALOG half only, not from the entry's own lines: an existing line must
  // stay correctable whatever it names, and a condition grade cannot legitimately
  // be on an entry anyway (ConditionGradeGuard refuses it server-side). Adding
  // one here would ask the server for a second lot on a grade its counter
  // already produced, and be rejected.
  function panelGrades(e: DailyEntry): EggGrade[] {
    const onEntry = new Set(e.grades.map((g) => g.eggGradeId));
    return grades.filter((g) =>
      onEntry.has(g.id) || (g.active && g.isSaleable && g.dailyEntryKind === "Manual"));
  }

  // #394: an adjustment has no draft state of its own — it replaces the
  // entry's official numbers outright, so it is held to the same exact
  // reconciliation Daily Entry's submit uses. Both screens read that rule out
  // of lib/grading, so the Save button's disabled state, the readouts in the
  // dialog and the submit-time guard below can never disagree.
  const gradesSum = Object.values(lineQty).reduce((sum, q) => sum + (q || 0), 0);
  const grading = gradingState({ totalEggs: total, cracked, dirty, discarded, gradesSum });
  const { losses, sellable, remaining, lossesExceedTotal } = grading;
  const gradesReconciled = grading.reconciled;
  const canAssign = remaining > 0;
  // DERIVED, not the raw flag: there is nothing left to hand out the instant
  // the day reconciles, and that can happen by typing a grade rather than by
  // using the gesture — which self-disarms. Reading `assigning` directly left
  // the rows armed (and Save enabled) for the render between that keystroke
  // and the effect below, so "armed" and "saveable" overlapped by one frame
  // instead of being mutually exclusive. Derived, they cannot (codex round 2).
  // The derivation itself lives in lib/grading, where it is asserted directly.
  const armed = armedState(assigning, canAssign);
  // The effect still clears the stale flag, so typing back DOWN to a remainder
  // does not silently re-arm rows the user disarmed by walking away from it.
  useEffect(() => {
    if (!canAssign && assigning) setAssigning(false);
  }, [canAssign, assigning]);

  // #443 — mirrors DailyEntryPage's setGrade: a grade edit that would push
  // the graded sum past what the total currently allows raises the total to
  // fit instead of being capped (the removed `max=` did that). Only ever
  // raises — never lowers — so trimming the total on step 1 is never fought.
  const setLine = (gradeId: string) => (next: number | ((prev: number) => number)) => {
    const current = lineQtyRef.current;
    const updated = {
      ...current,
      [gradeId]: typeof next === "function" ? next(current[gradeId] ?? 0) : next,
    };
    lineQtyRef.current = updated;
    setLineQty(updated);
    const newSum = Object.values(updated).reduce((a, b) => a + (b || 0), 0);
    setTotal((t) => Math.max(t, newSum + losses));
  };

  // Hand the whole remainder to one grade line.
  function assignRest(gradeId: string) {
    if (remaining <= 0) return;
    setLine(gradeId)((prev) => prev + remaining);
    setAssigning(false);
  }

  // Key lifecycle differs from the create screens (codex review of PR #81):
  // a SERVER response — success or rejection — is a definite outcome, so the
  // key rotates immediately and an edited retry is a fresh request (the
  // version base already guards against double-apply). Only a transport
  // failure (no response) keeps the key for an exact replay.
  function settleKey(scope: string, err?: unknown) {
    if (err === undefined || err instanceof ApiError) clearKey(scope);
  }

  // On a 409 the correction lost a race. Reload, then re-bind the panel to
  // the fresh entry with the OTHER admin's values in every field — keeping
  // this admin's typed numbers could silently clobber a grade line the
  // winner just added (pi review of PR #81). Only the reason survives; if
  // the entry is no longer correctable (voided meanwhile), close the panel.
  async function rebindAfterConflict(entryId: string) {
    try {
      await load();
      const fresh = await getDailyEntry(entryId);
      if (correctable(fresh)) {
        const keptReason = reason;
        startAdjust(fresh);
        setReason(keptReason);
        setError(i18n.t("history:conflictRebindMessage"));
      } else {
        setAdjusting(null);
        // .toLowerCase() on the raw wire status is locale-fragile (it only ever
        // reads correctly in English) — tracked as a native-pass deferral
        // (#182); interpolating the lowered value keeps this task
        // text-preserving without solving the shared-component lowercase
        // problem here.
        setError(i18n.t("history:nothingToAdjustMessage", { status: fresh.status.toLowerCase() }));
      }
    } catch {
      setError(i18n.t("history:conflictReloadFailedMessage"));
    }
  }

  async function onAdjustSubmit(ev: FormEvent) {
    ev.preventDefault();
    if (!adjusting || busy) return;
    setError(null);
    setMessage(null);
    const scope = `adjust:${adjusting.id}`;
    const lines = Object.entries(lineQty)
      .filter(([, q]) => q > 0)
      .map(([eggGradeId, quantity]) => ({ eggGradeId, quantity }));
    // Mirror the server's exact-reconciliation rule (#394) for an instant
    // message; the API remains the authority. The Save button is already
    // disabled while this is false (see gradesReconciled above), so this is
    // defense in depth rather than the primary gate. Validated before the
    // flight opens: a rejected form never reads as busy.
    if (!gradesReconciled) {
      setError(i18n.t("history:gradesMustReconcileMessage"));
      return;
    }
    await run(scope, async () => {
      try {
        await adjustDailyEntry(adjusting.id, {
          version: adjusting.version,
          totalEggs: total,
          crackedEggs: cracked,
          dirtyEggs: dirty,
          discardedEggs: discarded,
          mortalityCount: mortality,
          reason: reason.trim(),
          grades: lines, // [] explicitly clears all lines
        }, keyFor(scope));
        settleKey(scope);
        setAdjusting(null);
        setMessage(i18n.t("history:entryAdjustedMessage"));
        await load().catch(() =>
          setError(i18n.t("history:adjustReloadFailedMessage")));
      } catch (err) {
        settleKey(scope, err);
        if (err instanceof ApiError && err.status === 409) {
          await rebindAfterConflict(adjusting.id);
        } else {
          setError(errText(err));
        }
      }
    });
  }

  async function onVoid(e: DailyEntry) {
    // F13-style: the reason ask doubles as the confirmation. F135: it is the
    // app's own dialog, so the required check is inline and the typed text
    // survives it — window.prompt validated only after it had closed.
    const voidReason = await askReason({
      title: i18n.t("history:voidConfirmTitle", { date: e.date, flock: flockName(e.flockId) }),
      body: i18n.t("history:voidConfirmBody"),
      confirmLabel: i18n.t("history:voidConfirmLabel"),
      destructive: true,
    });
    if (voidReason === null) return;
    // The reason dialog settled BEFORE this flight opens (useConfirm
    // contract), so the originating row's void button is the pending
    // indicator from here to settle.
    const scope = `void:${e.id}`;
    await run(scope, async () => {
      setError(null);
      setMessage(null);
      try {
        await voidDailyEntry(e.id, { version: e.version, reason: voidReason }, keyFor(scope));
        settleKey(scope);
        // A stale adjust panel for the now-voided entry would only 409.
        if (adjusting?.id === e.id) setAdjusting(null);
        setMessage(i18n.t("history:entryVoidedMessage"));
        await load().catch(() =>
          setError(i18n.t("history:voidReloadFailedMessage")));
      } catch (err) {
        settleKey(scope, err);
        if (err instanceof ApiError && err.status === 409) {
          setError(i18n.t("history:voidConflictMessage"));
          await load().catch(() => setError(
            i18n.t("history:voidConflictReloadFailedMessage")));
        } else {
          setError(errText(err));
        }
      }
    });
  }

  function statusCell(e: DailyEntry) {
    // Colored status pills (#52). The three states with tooltips keep an
    // explicit <span> so the title survives (StatusBadge takes no title);
    // plain states (Submitted → ok, Draft → neutral) go through StatusBadge.
    // This pill's vocabulary (Draft/Submitted/Locked/ManagerAdjusted/Voided)
    // is DISTINCT from the shared `enums` status family — its display text
    // lives in this `history` namespace, not enums.ts (#182, Task 27).
    if (e.status === "Voided")
      return <span className="badge badge-danger" title={e.voidReason ?? undefined}>{t("statusVoided")}</span>;
    if (e.status === "ManagerAdjusted")
      return <span className="badge badge-warn" title={e.adjustReason ?? undefined}>{t("statusAdjusted")}</span>;
    if (e.status === "Locked")
      return (
        <span className="badge badge-accent"
          title={e.lockedAtUtc ? t("lockedAt", { time: e.lockedAtUtc }) : undefined}>
          {t("statusLocked")}
        </span>
      );
    return (
      <StatusBadge status={e.status}
        label={t(e.status === "Submitted" ? "statusSubmitted" : "statusDraft")} />
    );
  }

  if (error && entries === null) return <section><h2>{t("loadingTitle")}</h2><p className="error">{error}</p></section>;

  return (
    <section>
      <h2>{t("title")}</h2>
      {isAdmin && (
        <p className="muted">
          {t("intro")}
        </p>
      )}

      <div className="form-grid">
        <label>{t("flockLabel")}
          <select value={flockFilter} onChange={(e) => setFlockFilter(e.target.value)}>
            <option value="">{t("allFlocksOption")}</option>
            {flocks.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
          </select>
        </label>
        <label>{t("fromLabel")}
          <input type="date" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label>{t("toLabel")}
          <input type="date" value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
      </div>

      <Dialog
        open={adjusting !== null}
        title={adjusting
          ? t("adjustDialogTitleWithEntry", { date: adjusting.date, flock: flockName(adjusting.flockId) })
          : t("adjustDialogTitle")}
        onClose={() => setAdjusting(null)}
        // Two panes side by side need the room; on a phone the dialog is a
        // full-width sheet and they stack, exactly as the capture screen does.
        wide
        // A 409 swaps the server's newer entry into the open dialog; the record
        // identity changing pulls focus back to the first field, so the form is
        // not silently replaced under the user's cursor.
        focusKey={adjusting}
      >
        {adjusting && (
          <>
            {adjusting.adjustedFrom && (
              <p className="muted">
                {t("previouslyAdjusted", {
                  total: adjusting.adjustedFrom.totalEggs,
                  mortality: adjusting.adjustedFrom.mortalityCount,
                  // adjustReason is typed string | null; a set adjustedFrom
                  // always carries a reason in practice, but the fallback
                  // keeps this a plain string for the interpolation type.
                  reason: adjusting.adjustReason ?? "",
                })}
              </p>
            )}
            {/* The same two steps as the Daily entry screen, in the same order,
                reconciling the same way — a correction replaces the day's
                official numbers, so reading it should not be a different job
                from recording them. #250's steppers throughout. */}
            <form className="entry-form" onSubmit={onAdjustSubmit}>
            <div className="entry-cols">
              <section className="entry-step">
                {/* The word boundaries live in the h3's own text nodes, not at
                    the edges of the sr-only span: accessible-name computation
                    trims each nested element's contribution. */}
                <h3><span className="step-n">{te("stepLabel", { n: 1 })}</span> <span className="sr-only">{te("stepOfTotal")}</span> {te("eggCountsHeading")}</h3>
                <div className="entry-pane">
                  <div className="entry-rows">
                    {/* Sibling label, not wrapping — a <label> may not contain
                        interactive content other than its own control, and the
                        stepper carries two buttons. */}
                    <div className="entry-row">
                      <label htmlFor={idFor("total")}>{te("totalEggsLabel")}</label>
                      <NumberField id={idFor("total")} label={te("totalEggsLabel").toLowerCase()}
                        value={total} onChange={setTotal} />
                    </div>
                    <div className="entry-row">
                      <label htmlFor={idFor("cracked")}>{te("crackedLabel")}</label>
                      <NumberField id={idFor("cracked")} label={te("crackedLabel").toLowerCase()}
                        value={cracked} onChange={setCracked} />
                    </div>
                    <div className="entry-row">
                      <label htmlFor={idFor("dirty")}>{te("dirtyLabel")}</label>
                      <NumberField id={idFor("dirty")} label={te("dirtyLabel").toLowerCase()}
                        value={dirty} onChange={setDirty} />
                    </div>
                    <div className="entry-row">
                      <label htmlFor={idFor("discarded")}>{te("discardedLabel")}</label>
                      <NumberField id={idFor("discarded")} label={te("discardedLabel").toLowerCase()}
                        value={discarded} onChange={setDiscarded} />
                    </div>
                    <div className="entry-row">
                      <label htmlFor={idFor("mortality")}>{te("mortalityLabel")}</label>
                      <NumberField id={idFor("mortality")} label={te("mortalityLabel").toLowerCase()}
                        value={mortality} onChange={setMortality} />
                    </div>
                  </div>

                  {lossesExceedTotal ? (
                    <p className="entry-readout error">
                      {te("countsExceedTotalMessage", { losses: grading.losses, total })}
                    </p>
                  ) : (
                    /* Shown as a value, not buried in a sentence — it is the
                       target the grading pane has to hit. */
                    <p className="entry-readout">
                      <span className="k">{te("sellableLabel")}<br />{te("sellableFormula", { total, cracked, dirty, discarded })}</span>
                      <span className="v">{sellable}</span>
                    </p>
                  )}
                </div>
              </section>

              <section className="entry-step">
                <h3><span className="step-n">{te("stepLabel", { n: 2 })}</span> <span className="sr-only">{te("stepOfTotal")}</span> {te("gradingHeading")}</h3>
                <div className="entry-pane">
                  <div className="entry-rows">
                    {panelGrades(adjusting).map((g) => (
                      <div key={g.id} className={`entry-row${armed ? " taking" : ""}`}
                        {...remainderDropProps(armed, () => assignRest(g.id))}>
                        <label htmlFor={idFor(`grade-${g.id}`)}>{g.name}{g.active ? "" : t("inactiveGradeSuffix")}</label>
                        {/* #443 — no max=: same as the capture screen, the old
                            ceiling refused to let a grade run ahead of the
                            total. setLine raises the total to fit instead. */}
                        <NumberField id={idFor(`grade-${g.id}`)} label={g.name.toLowerCase()}
                          value={lineQty[g.id] ?? 0} onChange={setLine(g.id)} />
                        {armed && (
                          <TakeRemainderButton remaining={remaining} grade={g.name}
                            onTake={() => assignRest(g.id)} />
                        )}
                      </div>
                    ))}
                  </div>

                  {/* The same chip the capture screen uses — here it is also
                      exactly what the Save button is gated on (#394). */}
                  <GradingChip tone={grading.tone} count={grading.count}
                    says={te(grading.saysKey)}
                    canAssign={canAssign} remaining={remaining}
                    assigning={armed} onAssigningChange={setAssigning} />
                </div>
              </section>
            </div>

            <label className="entry-reason">{t("reasonLabel")}
              <input value={reason} maxLength={500} required
                onChange={(e) => setReason(e.target.value)} />
            </label>
            {/* The 409 rebind reports here, beside the form it asks you to re-apply. */}
            {error && <p className="error" role="alert">{error}</p>}
            <div className="dialog-foot">
              <button type="button" className="link" onClick={() => setAdjusting(null)}>{tc("cancel")}</button>
              {/* #394: an adjustment has no draft state — Save stays disabled
                  until grading reconciles exactly, the same rule Daily
                  Entry's submit uses. */}
              <BusyButton type="submit" busy={isPending(`adjust:${adjusting.id}`)}
                disabled={busy || !reason.trim() || !gradesReconciled}>{t("saveAdjustmentButton")}</BusyButton>
            </div>
            </form>
          </>
        )}
      </Dialog>

      {/* The dialog carries its own copy while it is up. */}
      {error && adjusting === null && <p className="error" role="alert">{error}</p>}
      {message && <p className="success" role="status">{message}</p>}

      {entries === null ? (
        <p className="muted">{tc("loading")}</p>
      ) : entries.length === 0 ? (
        <p className="muted">{t("noEntriesMatch")}</p>
      ) : (
        <>
          <table className="data">
            <thead>
              <tr>
                <th>{t("dateHeader")}</th><th>{t("flockHeader")}</th><th>{t("statusHeader")}</th><th>{t("totalHeader")}</th>
                <th>{t("lossesHeader")}</th>
                {/* #396 — Losses shows the cracked/dirty/discarded COUNTS
                    whatever became of them; this shows how many of those
                    actually became stock, per the entry's own snapshot. */}
                <th>{t("conditionHeader")}</th>
                <th>{t("mortalityHeader")}</th><th>{t("gradedHeader")}</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {entries.map((e) => (
                <tr key={e.id} className={e.status === "Voided" ? "inactive" : undefined}>
                  <td>{e.date}</td>
                  <td>{flockName(e.flockId)}</td>
                  <td>{statusCell(e)}</td>
                  <td>{e.totalEggs}</td>
                  <td>{e.crackedEggs}/{e.dirtyEggs}/{e.discardedEggs}</td>
                  <td>{conditionStock(e)}</td>
                  <td>{e.mortalityCount}</td>
                  <td>
                    {e.grades.length === 0
                      ? "—"
                      : e.grades.map((g) => `${gradeName(g.eggGradeId)} ${g.quantity}`).join(", ")}
                  </td>
                  <td>
                    {/* Drafts are edited on the Daily entry screen (#85) —
                        open to workers too; adjust/void stay admin-only. */}
                    {e.status === "Draft" && flockEditable(e.flockId) && (
                      <Link className="link"
                        to={`/daily-entry?flockId=${e.flockId}&date=${e.date}`}>
                        {t("editButton")}
                      </Link>
                    )}
                    {isAdmin && correctable(e) && (
                      <>
                        {/* Opens the dialog — the mutation's own trigger (and
                            its spinner) is the dialog's Save adjustment. */}
                        <button className="link" disabled={busy}
                          onClick={() => startAdjust(e)}>{t("adjustButton")}</button>
                        <BusyButton className="link" busy={isPending(`void:${e.id}`)}
                          disabled={busy}
                          onClick={() => void onVoid(e)}>{t("voidButton")}</BusyButton>
                      </>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {hasMore && (
            <button className="link" disabled={busy}
              onClick={() => void load(entries.length).catch(() => setError(i18n.t("history:loadMoreFailedMessage")))}>
              {t("loadMoreButton")}
            </button>
          )}
        </>
      )}

      {confirmDialog}
    </section>
  );
}
