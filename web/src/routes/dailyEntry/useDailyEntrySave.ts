import { useRef, useState } from "react";
import { recordDailyEntry, submitDailyEntry } from "../../api/cluckwork";
import type { EggGrade, Flock } from "../../api/cluckwork";
import type { PickerSnapshot } from "../../components/NamedEntityPicker";
import { useConfirm } from "../../components/useConfirm";
import { newId } from "../../lib/ids";
import i18n from "../../i18n";
import { errorMessage } from "./helpers";

interface SaveDeps {
  run: <T>(scope: string, action: (current: () => boolean) => Promise<T>) => Promise<T | undefined>;
  busy: boolean;
  setPageError: (text: string | null) => void;
  selectedFlock: Flock | undefined;
  flockSnapshot: PickerSnapshot<Flock>;
  prefillFailed: boolean;
  prefillPending: boolean;
  date: string;
  totalEggs: number;
  cracked: number;
  dirty: number;
  discarded: number;
  mortality: number;
  visibleGrades: EggGrade[];
  gradeQty: Record<string, number>;
  gradesTouched: boolean;
  setExistingStatus: (status: string) => void;
}

// The save/submit write of the daily-entry screen: the one shared flight
// (via the page's `run`), the confirm gate on submit, and the stable
// idempotency key. `confirmDialog` renders at the foot of the page, exactly
// where useConfirm's own slot sat before the move.
export function useDailyEntrySave({
  run, busy, setPageError, selectedFlock, flockSnapshot, prefillFailed,
  prefillPending, date, totalEggs, cracked, dirty, discarded, mortality,
  visibleGrades, gradeQty, gradesTouched, setExistingStatus,
}: SaveDeps) {
  const { confirm, confirmDialog } = useConfirm();
  // Stable idempotency keys per logical mutation: regenerated only after a
  // definitive success, so a retry after an ambiguous network failure dedupes
  // server-side instead of repeating the write.
  const saveKey = useRef<string>(newId());
  const [message, setMessage] = useState<string | null>(null);

  async function onSave(submit: boolean) {
    // #512 (T027) — the picker's safety state is the write guard, not the
    // button's disabled attribute: an exploring or unavailable picker must not
    // submit a stale committed ID even if the visible control is bypassed.
    if (busy || !selectedFlock || prefillFailed || prefillPending || !flockSnapshot.canSubmit) return;
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
      // The capture form is the page, not a dialog — its failure is the
      // page's.
      setPageError(null);
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
        setPageError(errorMessage(err));
      }
    });
  }

  return { message, onSave, confirmDialog };
}
