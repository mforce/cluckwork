import type { Dispatch, SetStateAction } from "react";
import { useTranslation } from "react-i18next";
import { Stack, TextField } from "@mui/material";
import { workerSaleAllocationPolicyLabel, WORKER_SALE_ALLOCATION_POLICY_VALUES } from "../../i18n/enums";
import type { EggUnitConversion } from "../../api/cluckwork";

export function CountingSalesSection({
  defaultStepperUnit, setDefaultStepperUnit, stepperUnits, workerSaleAllocationPolicy,
  setWorkerSaleAllocationPolicy, maxDiscountPercent, setMaxDiscountPercent,
}: {
  defaultStepperUnit: string;
  setDefaultStepperUnit: Dispatch<SetStateAction<string>>;
  stepperUnits: EggUnitConversion[];
  workerSaleAllocationPolicy: string;
  setWorkerSaleAllocationPolicy: Dispatch<SetStateAction<string>>;
  maxDiscountPercent: string;
  setMaxDiscountPercent: Dispatch<SetStateAction<string>>;
}) {
  const { t } = useTranslation("settings");

  return (
      <Stack spacing={2} sx={{ maxWidth: "40rem" }}>

        {/* #444 — the pack unit Daily Entry's steppers bump by, e.g. "+30/-30"
            for Tray, unless a user overrides it for themselves (Header). Codes
            render raw, untranslated, matching ProductsPage's own unitCode cells
            — there is no separate label catalog for this enum anywhere else. */}
        <TextField select label={t("defaultStepperUnitLabel")} value={defaultStepperUnit}
          onChange={(e) => setDefaultStepperUnit(e.target.value)}
          helperText={t("defaultStepperUnitHint")}
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}>
          {stepperUnits.filter((u) => u.active).map((u) =>
            <option key={u.unitCode} value={u.unitCode}>{u.unitCode}</option>)}
        </TextField>

        {/* #612 — how a restricted plain Worker's sale confirmation may draw
            stock. Owner/Manager/Sales confirmations stay farm-wide regardless
            of this setting (ReadOnly cannot confirm at all). */}
        <TextField select label={t("workerSaleAllocationPolicyLabel")} value={workerSaleAllocationPolicy}
          onChange={(e) => setWorkerSaleAllocationPolicy(e.target.value)}
          helperText={t("workerSaleAllocationPolicyHint")}
          slotProps={{ select: { native: true }, inputLabel: { shrink: true } }}>
          {WORKER_SALE_ALLOCATION_POLICY_VALUES.map((p) =>
            <option key={p} value={p}>{workerSaleAllocationPolicyLabel(p)}</option>)}
        </TextField>

        {/* #727 — the ceiling a Sales or Worker user's sale lines are held to.
            Whole percents: this is the screen's only numeric input, and
            type="number" disagrees with itself across browsers about `,`
            versus `.` on a screen that formats every other number by locale.
            Storage is basis points, so finer steps cost no migration. */}
        <TextField
          label={t("maxDiscountPercentLabel")}
          type="number"
          value={maxDiscountPercent}
          onChange={(e) => setMaxDiscountPercent(e.target.value)}
          helperText={t("maxDiscountPercentHint")}
          sx={{ maxWidth: "12rem" }}
          slotProps={{ htmlInput: { min: 0, max: 100, step: 1 } }}
        />
      </Stack>
  );
}
