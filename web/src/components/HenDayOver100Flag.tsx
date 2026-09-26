import { Box, Tooltip } from "@mui/material";
import { TriangleAlert } from "lucide-react";
import { useTranslation } from "react-i18next";

// #943 — a hen-day % above 100 is impossible (a hen lays at most one egg a
// day): a filing and the bird ledger disagree about that flock's birds. The
// figure is shown as computed, so it still reproduces from Rated eggs ÷
// Recorded hen-days, and this marks it instead of capping it to a number the
// report would not reproduce.
export function HenDayOver100Flag({ pct }: { pct: number | null }) {
  const { t } = useTranslation("reports");
  if (pct === null || pct <= 100) return null;
  const label = t("henDayOver100");
  return (
    <Tooltip title={label}>
      <Box component="span" role="img" aria-label={label} sx={{ color: "warning.main", ml: 0.5, verticalAlign: "middle", display: "inline-flex" }}>
        <TriangleAlert size={16} aria-hidden />
      </Box>
    </Tooltip>
  );
}
