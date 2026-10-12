import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Box, Card, LinearProgress, Typography } from "@mui/material";
import { Bird } from "lucide-react";
import type { DailyEntry } from "../../api/cluckwork";
import type { Dispatch, ReactNode, SetStateAction } from "react";
import { useFormat } from "../../farm/useFormat";
import { EmptyState } from "../../components/EmptyState";
import { PanelPager } from "../../components/PanelPager";
import { panelPage, todaysEggs } from "../../lib/dashboard";
import type { CaptureTile } from "../../lib/dashboard";
import { headingSx, sectionSx } from "./panelSx";
import { TodayRow } from "./TodayRow";

// The Today collection panel of the Dashboard (#1215): per-flock capture
// status with its progress bar, pager and totals footer. It renders at
// exactly the page's old position in the panel grid; the houses page state
// and its reset stay on the page and are passed in.
export function CollectionPanel({
  allTiles,
  entries,
  flocksFailed,
  entriesFailed,
  panelError,
  housesIncomplete,
  entryDataIncomplete,
  recordedHouses,
  housesPage, setHousesPage,
  housesPerPage,
  today,
  t, fmt,
  yesterdayClose,
}: {
  allTiles: CaptureTile[] | null;
  entries: DailyEntry[] | null;
  flocksFailed: boolean;
  entriesFailed: boolean;
  panelError: ReactNode;
  housesIncomplete: boolean;
  entryDataIncomplete: boolean;
  recordedHouses: number;
  housesPage: number; setHousesPage: Dispatch<SetStateAction<number>>;
  housesPerPage: number;
  today: string;
  t: ReturnType<typeof useTranslation<"dashboard">>["t"];
  fmt: ReturnType<typeof useFormat>;
  yesterdayClose: number | null;
}) {
  const housePage = panelPage(allTiles ?? [], housesPage, housesPerPage);
  const housesPagerLabel = housesIncomplete
    ? t("pagerHousesAtLeast", {
        first: fmt.count(housePage.first), last: fmt.count(housePage.last), total: fmt.count(housePage.total),
      })
    : t("pagerHouses", {
        first: fmt.count(housePage.first), last: fmt.count(housePage.last), total: fmt.count(housePage.total),
      });
  return (
    <Card component="section" sx={sectionSx}>
      <Box sx={headingSx}>
        <Typography variant="h3" aria-label={t("todayPanelTitle")}><Link to="/daily-entry">{t("collectionTitle")}</Link></Typography>
        {allTiles !== null && <Typography variant="caption" color="text.secondary" sx={{ textAlign: "right" }}>
          {housesIncomplete
            ? t("todayInCountAtLeast", { in: recordedHouses, count: allTiles.length })
            : t("todayInCount", { in: recordedHouses, count: allTiles.length })}
        </Typography>}
      </Box>
      {allTiles === null || entries === null
        ? (flocksFailed || entriesFailed ? panelError
          : <LinearProgress aria-label={t("collectionTitle")} sx={{ height: 5, borderRadius: 2 }} />)
        : allTiles.length === 0 ? (
        <EmptyState icon={Bird} message={t("noFlocksMessage")} />
      ) : (
        <>
          {/* A share of an unknown whole is not a share. With the list
              incomplete the bar carries no value at all rather than one
              measured against a ceiling. */}
          <LinearProgress
            variant={housesIncomplete ? "indeterminate" : "determinate"}
            value={housesIncomplete ? undefined : recordedHouses / allTiles.length * 100}
            aria-label={t("collectionTitle")}
            sx={{ height: 5, borderRadius: 2, mb: 1, bgcolor: "var(--surface-2)", "& .MuiLinearProgress-bar": { bgcolor: "var(--success)" } }} />
          {housePage.items.map((tile) => <TodayRow key={tile.flock.id} tile={tile} today={today} fmt={fmt} t={t} />)}
          {housePage.pageCount > 1 && <PanelPager
            label={housesPagerLabel}
            previousLabel={t("pagerPrevious", { panel: t("collectionTitle") })}
            nextLabel={t("pagerNext", { panel: t("collectionTitle") })}
            hasPrevious={housePage.page > 0}
            hasNext={housePage.page + 1 < housePage.pageCount}
            onPrevious={() => setHousesPage(housePage.page - 1)}
            onNext={() => setHousesPage(housePage.page + 1)}
          />}
          <Box sx={{ bgcolor: "var(--surface-2)", mx: { xs: -2, md: -2.25 }, mb: { xs: -2, md: -2.25 }, mt: 1.5, px: 2.25, py: 1.5 }}>
            <Box sx={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
              <Typography>{t("collectedToday")}</Typography>
              <Typography className="num" sx={{ fontFamily: "Georgia, serif", fontWeight: 600, fontSize: "1.8rem" }}>{fmt.count(todaysEggs(entries))}</Typography>
            </Box>
            {yesterdayClose !== null && <Typography variant="caption" color="text.secondary">{t("yesterdayByClose", { total: fmt.count(yesterdayClose) })}</Typography>}
            {housesIncomplete && <Typography variant="caption" color="text.secondary" sx={{ display: "block" }}>
              {t("housesIncompleteNotice", { count: allTiles.length, total: fmt.count(allTiles.length) })}
            </Typography>}
            {entryDataIncomplete && <Typography variant="caption" color="text.secondary" sx={{ display: "block" }}>
              {t("entriesIncompleteNotice")}
            </Typography>}
          </Box>
        </>
      )}
    </Card>
  );
}
