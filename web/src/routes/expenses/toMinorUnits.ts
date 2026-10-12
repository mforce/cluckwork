import i18n from "../../i18n";

// Exact decimal parsing — float multiplication silently mis-rounds edge
// amounts and hides excess decimals; the minor unit is the CALLER's because
// an old expense keeps its snapshotted denomination, which may differ from
// the account's current one (codex review of #88).
export const toMinorUnits = (display: string, minor: number) => {
  const m = display.trim().match(/^(\d+)(?:\.(\d+))?$/);
  if (!m) throw new Error(i18n.t("expenses:enterValidAmount"));
  const frac = m[2] ?? "";
  if (frac.length > minor)
    throw new Error(minor === 0
      ? i18n.t("expenses:noDecimalPlaces")
      : i18n.t("expenses:atMostDecimals", { count: minor }));
  const v = Number(m[1]) * 10 ** minor + Number(frac.padEnd(minor, "0") || "0");
  if (!Number.isSafeInteger(v) || v <= 0) throw new Error(i18n.t("expenses:enterAmountGreaterThanZero"));
  return v;
};
