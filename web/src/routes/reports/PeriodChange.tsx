import { Typography } from "@mui/material";
import { useTranslation } from "react-i18next";
import { useFormat } from "../../farm/useFormat";
import { periodChange } from "./periodComparison";

export function PeriodChange({ current, previous, days, rate = false }: {
  current: number | null;
  previous: number | null | undefined;
  days: number;
  rate?: boolean;
}) {
  const { t } = useTranslation("reports");
  const fmt = useFormat();
  const change = periodChange(current, previous, rate);
  if (change === null) return null;
  const rounded = Math.round(change * 10) / 10 || 0;
  const value = `${rounded > 0 ? "+" : ""}${fmt.count(rounded, 1)}`;
  return (
    <Typography component="span" sx={{ display: "block", fontSize: ".7rem", color: "text.secondary", fontWeight: 400 }}>
      {t(rate ? "previousPoints" : "previousPercent", { value, days: fmt.count(days), count: days })}
    </Typography>
  );
}
