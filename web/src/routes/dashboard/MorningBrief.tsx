import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Box, Typography } from "@mui/material";
import { TriangleAlert } from "lucide-react";
import type { DailyEntry, Flock, StockRow } from "../../api/cluckwork";
import { useFormat } from "../../farm/useFormat";
import { todaysEggs } from "../../lib/dashboard";
import type { CaptureTile } from "../../lib/dashboard";

// The morning brief of the Dashboard (#1215): the attention line and the
// low-stock fact beside it, with today's total on the right. It renders at
// exactly the page's old position, above the panel grid; the attention cap,
// the below-floor rows and the fact string stay on the page and are passed in.
export function MorningBrief({
  allTiles,
  missingHouses,
  attentionShown,
  attentionMore,
  belowFloorRows,
  belowFloorFact,
  entryDataIncomplete,
  entries,
  t, fmt,
}: {
  allTiles: CaptureTile[] | null;
  missingHouses: Flock[];
  attentionShown: Flock[];
  attentionMore: number;
  belowFloorRows: StockRow[];
  belowFloorFact: (key: "attentionGradeBelowFloor" | "stockCaptionBelowFloor") => string;
  entryDataIncomplete: boolean;
  entries: DailyEntry[] | null;
  t: ReturnType<typeof useTranslation<"dashboard">>["t"];
  fmt: ReturnType<typeof useFormat>;
}) {
  return (
    <Box component="section" aria-label={t("morningBrief")} sx={(theme) => ({
      display: "grid", gridTemplateColumns: { xs: "1fr", md: "minmax(0,1fr) auto" }, gap: 2,
      p: 2.5, mb: 3, borderRadius: "var(--r-panel)", border: "1px solid var(--rule)",
      bgcolor: theme.palette.mode === "dark" ? "var(--surface)" : "var(--ink)",
      color: theme.palette.mode === "dark" ? "var(--ink)" : "var(--surface)",
    })}>
      <Box>
        <Typography variant="h3" sx={{ fontFamily: "Georgia, serif", fontSize: "1.4rem", mb: 1 }}>{t("morningBrief")}</Typography>
        <Box sx={{ display: "flex", flexWrap: "wrap", columnGap: 2, rowGap: 0.5 }}>
          {allTiles === null ? <Typography>{t("panelLoadError")}</Typography> : missingHouses.length > 0 ? (
            <>
              {attentionShown.map((flock) => (
                <Typography key={flock.id} sx={{ display: "flex", alignItems: "center", gap: 1 }}>
                  <TriangleAlert size={15} aria-hidden />{t("attentionHouseNotRecorded", { flock: flock.name })}
                </Typography>
              ))}
              {attentionMore > 0 && <Typography component={Link} to="/daily-entry" sx={{ color: "inherit" }}>{t("attentionMore", { count: attentionMore })}</Typography>}
            </>
          ) : <Typography>{t(allTiles.length === 0 ? "noFlocksMessage"
            : entryDataIncomplete ? "entriesIncompleteBrief" : "allHousesRecorded")}</Typography>}
          {belowFloorRows.length > 0 && (
            <Typography component={Link} to="/stock" sx={{ color: "inherit", display: "flex", alignItems: "center", gap: 1 }}>
              <TriangleAlert size={15} aria-hidden />{belowFloorFact("attentionGradeBelowFloor")}
            </Typography>
          )}
        </Box>
      </Box>
      {allTiles !== null && entries !== null && <Box sx={{ borderLeft: { md: "1px solid var(--rule)" }, borderTop: { xs: "1px solid var(--rule)", md: 0 }, pl: { md: 2.5 }, pt: { xs: 1.5, md: 0 }, minWidth: 150 }}>
        <Typography variant="caption">{t("todaySoFarLabel")}</Typography>
        <Typography className="num" sx={{ fontFamily: "Georgia, serif", fontSize: "2rem", fontWeight: 600, lineHeight: 1.15 }}>
          {entries === null ? "—" : fmt.count(todaysEggs(entries))}
        </Typography>
      </Box>}
    </Box>
  );
}
