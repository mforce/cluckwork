import type { Dispatch, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import type { resolveStepperUnit } from "../../lib/stepperUnit";
import { statusLabel } from "../../i18n/enums";

interface EntryBannersProps {
  entryLocked: boolean;
  existingStatus: string | null;
  prefillFailed: boolean;
  setPrefillRetry: Dispatch<SetStateAction<number>>;
  stepSize: number;
  stepperUnit: ReturnType<typeof resolveStepperUnit>;
}

// The locked / prefill-failed / stepper-unit banners of the daily-entry
// screen, in their original order and conditions.
export function EntryBanners({
  entryLocked, existingStatus, prefillFailed, setPrefillRetry, stepSize,
  stepperUnit,
}: EntryBannersProps) {
  const { t } = useTranslation(["dailyEntry", "dashboard"]);
  const { t: tc } = useTranslation("common");

  return (
    <>
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
    </>
  );
}
