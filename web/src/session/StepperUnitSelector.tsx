import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { listEggUnitConversions, putMeStepperUnit } from "../api/cluckwork";
import type { EggUnitConversion } from "../api/cluckwork";
import { usePendingAction } from "../components/usePendingAction";
import { useFarm } from "../farm/useFarm";
import { useMe, useMeUpdate } from "../session/SessionContext";

// Per-user Daily Entry stepper pack unit (#444), beside the language selector
// on the Account screen. "" (the farm-default option) maps to null on the wire
// — the preference is CLEARED, not set to whatever the farm default currently
// is, so a later farm-default change follows the user automatically.
//
// Unlike LanguageSelector (which switches i18next directly and reconciles from
// /me on the next bootstrap), this preference is read live through useMe() by
// DailyEntryPage — so the optimistic local application goes through
// useMeUpdate() instead. The server persist is fire-and-forget the same way.
export function StepperUnitSelector() {
  const { t } = useTranslation("account");
  const me = useMe();
  const patchMe = useMeUpdate();
  const { farm } = useFarm();
  const { busy, run } = usePendingAction();
  const [units, setUnits] = useState<EggUnitConversion[]>([]);

  useEffect(() => {
    let cancelled = false;
    listEggUnitConversions()
      .then((list) => { if (!cancelled) setUnits(list.filter((u) => u.active)); })
      // Options stay empty; the select still renders the farm-default option,
      // so nothing breaks — the user just cannot pick an override right now.
      .catch(() => {});
    return () => { cancelled = true; };
  }, []);

  const current = me?.preferredStepperUnit ?? "";
  const [saveFailed, setSaveFailed] = useState(false);
  // A stored override can name a unit the farm has since deactivated. Without
  // its own option the <select value> matches nothing and the browser renders
  // the FIRST option as selected while the real value is something else —
  // same hazard DailyEntryPage's visibleGrades solves for deactivated grades
  // (pi review of #451). Keeping it visible also means the user can SEE what
  // their stale preference is before clearing or replacing it; the stepper
  // itself already falls back to counting by 1 (resolveStepperUnitSize).
  const staleOverride = current !== "" && !units.some((u) => u.unitCode === current);
  const onChange = (e: React.ChangeEvent<HTMLSelectElement>) => {
    const unit = e.target.value === "" ? null : e.target.value;
    // Optimistic, same as the language switch — the screen the preference
    // drives updates immediately. UNLIKE the language switch, a failed persist
    // rolls the patch back (codex P2 review of #451): the language's local
    // i18next switch is genuinely local and harmlessly reconciles at the next
    // bootstrap, but a rejected stepper preference left in MeContext would
    // keep every Daily Entry stepper counting by a unit the server refused —
    // invisibly, until the next login.
    const previous = me?.preferredStepperUnit ?? null;
    setSaveFailed(false);
    patchMe({ preferredStepperUnit: unit });
    void run("stepper-unit", () => putMeStepperUnit(unit)).catch((err) => {
      console.warn("Failed to persist stepper-unit preference", err);
      patchMe({ preferredStepperUnit: previous });
      setSaveFailed(true);
    });
  };

  return (
    <label className="field">
      <span>{t("stepperUnit")}</span>
      <select value={current} onChange={onChange} disabled={busy}>
        <option value="">
          {t("stepperUnitFarmDefaultOption", { unit: farm?.defaultStepperUnit ?? "Individual" })}
        </option>
        {units.map((u) => (
          <option key={u.unitCode} value={u.unitCode}>{u.unitCode}</option>
        ))}
        {staleOverride && <option value={current}>{current}</option>}
      </select>
      {saveFailed && <span className="error" role="alert">{t("stepperUnitSaveFailed")}</span>}
    </label>
  );
}
