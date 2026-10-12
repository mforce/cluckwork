import { useEffect, useEffectEvent, useId, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Box } from "@mui/material";
import {
  listDailyEntries,
  listEggGrades,
  listEggUnitConversions,
  listFeedUsage,
  listFlocks,
  listWaterUsage,
} from "../api/cluckwork";
import type { EggGrade, EggUnitConversion, FeedUsage, Flock, WaterUsage } from "../api/cluckwork";
import { rememberFlockId, resolveDefaultFlock } from "../lib/flockDefault";
import type { PickerSnapshot } from "../components/NamedEntityPicker";
import { useDialogAction } from "../components/useDialogAction";
import { useAuth } from "../auth/useAuth";
import { useFarm, useFarmToday } from "../farm/useFarm";
import { gradingState } from "../lib/grading";
import { resolveStepperUnit } from "../lib/stepperUnit";
import { useMe } from "../session/SessionContext";
import i18n from "../i18n";
import { CollectionPanel } from "./dailyEntry/CollectionPanel";
import { DaySupportLine } from "./dailyEntry/DaySupportLine";
import { EntryBanners } from "./dailyEntry/EntryBanners";
import { EntryContextRow } from "./dailyEntry/EntryContextRow";
import { EntryFooter } from "./dailyEntry/EntryFooter";
import { EntryHeader } from "./dailyEntry/EntryHeader";
import { GradingPanel } from "./dailyEntry/GradingPanel";
import { capturable, DIALOG_SCOPES } from "./dailyEntry/helpers";
import { useDailyEntrySave } from "./dailyEntry/useDailyEntrySave";
import { useNewFlock } from "./dailyEntry/useNewFlock";

// F1 (#21): record the day's production by grade, then submit — submitting
// turns grade lines into egg lots (stock).
export function DailyEntryPage() {
  // #830 — the state line reuses Dashboard's own "Draft, saved {{time}}" /
  // "+{{count}} more" catalog entries (dashboard:entryStateDraftTime,
  // dashboard:attentionMore) rather than a second copy of the same English
  // and already-translated es/tl strings.
  const { t } = useTranslation(["dailyEntry", "dashboard"]);
  const { t: tc } = useTranslation("common");
  // Farm-local, not browser-local: since #35 the API judges "is this date in
  // the future?" against the FARM's day, so the pickers must agree (#123).
  const today = useFarmToday();
  // Flock creation is Owner/Manager administration (#388): a scoped Worker
  // cannot assign the flock it just created.
  const { isAdmin } = useAuth();
  const [loading, setLoading] = useState(true);
  const [flocks, setFlocks] = useState<Flock[]>([]);
  const [grades, setGrades] = useState<EggGrade[]>([]);
  // #444 — the stepper's base increment resolves from farm default + user
  // override, both already fetched by the shell (useFarm/useMe); only the
  // conversion catalog is this screen's own read.
  const [eggUnitConversions, setEggUnitConversions] = useState<EggUnitConversion[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [flockId, setFlockId] = useState("");
  const [date, setDate] = useState(today);
  // #512 (T027/T036) — the capture flock is committed through FlockPicker.
  // `pickerFlock` is the page-controlled committed entity; bumping
  // `pickerFlockGen` makes the engine re-sync its committed state (deep link,
  // remembered, default, new-flock create — every external reset goes through
  // here so an Escape or later exploration can never resurrect a stale ID).
  // `flockSnapshot.canSubmit` gates BOTH the visible save controls AND the
  // save handlers — a disabled button alone is not the write-safety boundary.
  //
  // INITIAL STATE: `canSubmit: true`. The picker's snapshot effect fires on
  // mount and replaces this with the engine's truth (committed → true,
  // exploring/unavailable → false). The save buttons are ALSO gated on
  // `!flockId` (no id = no write), so the brief window before the first
  // snapshot (flockId empty, canSubmit true) is inert: the handler's own
  // `!selectedFlock` and `!flockSnapshot.canSubmit` checks both hold. A
  // test that asserts the save button is disabled before the first snapshot
  // would be asserting a transient render, not a safety property — the write
  // guard is the handler's `!flockSnapshot.canSubmit` check, which reads the
  // LIVE snapshot at call time, not this initial.
  const [pickerFlock, setPickerFlock] = useState<Flock | null>(null);
  const [pickerFlockGen, setPickerFlockGen] = useState(0);
  const [flockSnapshot, setFlockSnapshot] = useState<PickerSnapshot<Flock>>({
    committed: null, selectionPhase: "uninitialized", exploring: false, canSubmit: true,
  });
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
  // #830 — the state line under the title ("Draft, saved 06:52"). Read-only
  // display of what the prefill already fetched (RecordHistory's own
  // timestamps), never a new write path: the last-changed time if the day was
  // ever edited, else when it was first created. `null` (pre-#494 rows with
  // no history, or no existing entry) degrades to the bare status word.
  const [existingSavedAt, setExistingSavedAt] = useState<string | null>(null);
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

  // One shared flight for save/submit/create-flock (#236), plus the per-place
  // message slots (#479) and the new-flock dialog session (#477 part 2), from
  // one shared hook. Only "new-flock" owns a dialog; save/submit route to the
  // page and are always current.
  const { busy, isPending, errors, run, openDialog, dismissDialog } = useDialogAction(DIALOG_SCOPES);
  // #479 — one slot per PLACE a message can appear: the deep-link check and
  // the draft/submit writes (the main form, not behind a dialog) belong to
  // the page; the new-flock dialog's failures belong to that form.
  const setPageError = errors.setPage;

  // #1168 — read when the mount load SETTLES, not when it starts: a load still
  // in flight across the farm's midnight must not reject a link to the new day.
  const farmToday = useEffectEvent(() => today);

  useEffect(() => {
    // includeInactive: an existing draft may reference a since-deactivated
    // grade; that line must render and survive a re-save (only the ACTIVE
    // saleable grades are offered for new input — see visibleGrades).
    Promise.all([listFlocks(), listEggGrades({ includeInactive: true }), listEggUnitConversions()])
      .then(async ([all, g, units]) => {
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
          && wantedDate <= farmToday();
        const flockOk = wantedFlock !== null && f.some((x) => x.id === wantedFlock);
        const deepLinked = flockOk && dateOk;
        if (deepLinked) retarget(() => setDate(wantedDate!));
        else if (wantedFlock || wantedDate)
          setPageError(i18n.t("dailyEntry:deepLinkUnavailable"));
        // #512 (T036) — the picker's committed entity is set HERE, in the same
        // mount-time effect that resolves the deep-link/remembered/default
        // precedence. Bumping `pickerFlockGen` makes the engine's controlled
        // sync effect fire, committing the entity and flipping canSubmit.
        //
        // #646 — the non-deep-link branches moved into resolveDefaultFlock:
        // remembered first, then the first ACTIVE flock, then a depleted one.
        // Two things changed by moving it there. A remembered flock OUTSIDE
        // the capped page used to be dropped silently (`f.some(...)` could not
        // see it) and is now resolved by an exact GET, the same way the
        // pickers resolve an out-of-window id; and the old `?? f[0]` last
        // resort, which could hand back an ARCHIVED flock nobody can record
        // against, is gone. The await is safe here: the screen renders its
        // loading state until the `finally` below, so there is no form to race.
        const targetFlock = deepLinked
          ? f.find((x) => x.id === wantedFlock)
          : await resolveDefaultFlock(f);
        if (deepLinked) retarget(() => setFlockId(wantedFlock!));
        else if (targetFlock) retarget(() => setFlockId(targetFlock.id));
        if (targetFlock) {
          setPickerFlock(targetFlock);
          setPickerFlockGen((g) => g + 1);
        }
      })
      .catch(() => setLoadError(i18n.t("dailyEntry:loadFlocksGradesFailed")))
      .finally(() => setLoading(false));
  }, [setPageError]);

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
          setExistingSavedAt(existing.lastChangedAtUtc ?? existing.createdAtUtc);
        } else if (!isRetryRecovery) {
          setTotalEggs(0); setCracked(0); setDirty(0); setDiscarded(0); setMortality(0);
          setGradeQty({});
          setGradesTouched(false);
          setExistingStatus(null);
          setExistingSavedAt(null);
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
    if (flockId) rememberFlockId(flockId);
  }, [flockId]);

  // #512 (T036) — GET-only post-create hydration. A freshly created flock is
  // admitted as-is (the POST already returned the full typed entity, so the
  // engine commits it without a read). If a subsequent exact read for that id
  // fails (the picker's unavailable state), the picker's own Retry re-issues
  // ONLY the GET (the create POST is never repeated) via its internal
  // `retryUnavailable`, wired to the unavailable-state Retry button.

  // #446 — see the daySupport state comment for why this effect is isolated.
  useEffect(() => {
    if (!flockId || !date) { setDaySupport(null); return; }
    let cancelled = false;
    setDaySupport(null);
    // The strip presents COUNTS and a cost SUM, so it must drain every page —
    // a single limit-100 read would silently underreport a heavy day.
    const PAGE = 100;
    const drain = async <T,>(fetchPage: (offset: number) => Promise<T[]>) => {
      const all: T[] = [];
      for (let offset = 0; ; offset += PAGE) {
        const page = await fetchPage(offset);
        all.push(...page);
        if (page.length < PAGE) return all;
      }
    };
    Promise.all([
      drain((offset) => listFeedUsage({ flockId, from: date, to: date, limit: PAGE, offset })),
      drain((offset) => listWaterUsage({ flockId, from: date, to: date, limit: PAGE, offset })),
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
  const selectedFlock = pickerFlock ?? flocks.find((f) => f.id === flockId);
  const entryLocked = existingStatus !== null && existingStatus !== "Draft";
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

  // #512 (T036) — post-create hydration lands here. The engine resolves
  // `requestedId` (the created id, since `pickerFlock` was left null) through
  // its own exact GET and reports the resolved entity via `committed`; this
  // mirrors it onto `pickerFlock` so the closed-state trigger — which reads
  // `pickerFlock` directly, not the snapshot — shows it. Scoped to the id the
  // page is actually targeting, so a stale/superseded snapshot can never
  // resurrect a flock the page has since moved on from.
  function handleFlockSnapshot(s: PickerSnapshot<Flock>) {
    setFlockSnapshot(s);
    if (s.committed && s.committed.id === flockId && s.committed.id !== pickerFlock?.id) {
      setPickerFlock(s.committed);
    }
  }

  // NumberField owns its own input, so the row label points at it by id.
  const fieldId = useId();
  const idFor = (name: string) => `${fieldId}-${name}`;

  // The new-flock dialog and its create write moved to dailyEntry/useNewFlock;
  // it renders the page's one dialog session and disarms through `retarget`.
  const { setShowNewFlock, dialog: newFlockDialog } = useNewFlock({
    run, busy, isPending, errors, dismissDialog,
    idFor, retarget, setFlockId, setPickerFlock, setPickerFlockGen, setFlocks,
  });

  // The save/submit flight, its idempotency key and its message slot moved to
  // dailyEntry/useDailyEntrySave; confirmDialog renders at the foot as before.
  const { message, onSave, confirmDialog } = useDailyEntrySave({
    run, busy, setPageError, selectedFlock, flockSnapshot, prefillFailed,
    prefillPending, date, totalEggs, cracked, dirty, discarded, mortality,
    visibleGrades, gradeQty, gradesTouched, setExistingStatus,
  });

  if (loading) return <section><h2>{t("title")}</h2><p className="muted">{tc("loading")}</p></section>;
  if (loadError) return <section><h2>{t("title")}</h2><p className="error">{loadError}</p></section>;

  return (
    <Box sx={{ maxWidth: 1120 }}>
      <EntryHeader
        selectedFlock={selectedFlock}
        existingStatus={existingStatus}
        prefillPending={prefillPending}
        prefillFailed={prefillFailed}
        existingSavedAt={existingSavedAt}
        mortality={mortality}
        entryLocked={entryLocked}
      />

      <EntryContextRow
        pickerFlock={pickerFlock}
        pickerFlockGen={pickerFlockGen}
        flockId={flockId}
        flocks={flocks}
        handleFlockSnapshot={handleFlockSnapshot}
        retarget={retarget}
        setFlockId={setFlockId}
        setPickerFlock={setPickerFlock}
        setPickerFlockGen={setPickerFlockGen}
        date={date}
        setDate={setDate}
        today={today}
        isAdmin={isAdmin}
        openDialog={openDialog}
        setShowNewFlock={setShowNewFlock}
      />

      {newFlockDialog}

      <EntryBanners
        entryLocked={entryLocked}
        existingStatus={existingStatus}
        prefillFailed={prefillFailed}
        setPrefillRetry={setPrefillRetry}
        stepSize={stepSize}
        stepperUnit={stepperUnit}
      />

      <DaySupportLine daySupport={daySupport} flockId={flockId} date={date} />

      {/* The two work cards stay side by side when space allows because the
          collection result is the grading target. */}
      <Box sx={{ display: "grid", gridTemplateColumns: { xs: "1fr", md: "repeat(2, minmax(0, 1fr))" }, gap: { xs: 3.5, md: 4 }, mt: { xs: 3.5, md: 4 } }}>
        <CollectionPanel
          losses={losses}
          lossesExceedTotal={lossesExceedTotal}
          sellable={sellable}
          totalEggs={totalEggs}
          setTotalEggs={setTotalEggs}
          cracked={cracked}
          setCracked={setCracked}
          dirty={dirty}
          setDirty={setDirty}
          discarded={discarded}
          setDiscarded={setDiscarded}
          mortality={mortality}
          setMortality={setMortality}
          entryLocked={entryLocked}
          idFor={idFor}
          stepSize={stepSize}
        />

        <GradingPanel
          assigning={assigning}
          setAssigning={setAssigning}
          remaining={remaining}
          entryLocked={entryLocked}
          prefillPending={prefillPending}
          prefillFailed={prefillFailed}
          setGradesTouched={setGradesTouched}
          setTotalEggs={setTotalEggs}
          setGradeQty={setGradeQty}
          losses={losses}
          gradeQty={gradeQty}
          visibleGrades={visibleGrades}
          gradesSum={gradesSum}
          idFor={idFor}
          stepSize={stepSize}
          grading={grading}
          sellable={sellable}
          lossesExceedTotal={lossesExceedTotal}
        />
      </Box>

      <EntryFooter
        errors={errors}
        message={message}
        onSave={onSave}
        busy={busy}
        isPending={isPending}
        flockId={flockId}
        flockSnapshot={flockSnapshot}
        grading={grading}
        entryLocked={entryLocked}
        prefillFailed={prefillFailed}
        prefillPending={prefillPending}
      />

      {confirmDialog}
    </Box>
  );
}

