import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Box, Card, LinearProgress, Table, TableBody, TableCell, TableHead, TableRow, Typography } from "@mui/material";
import { Egg, TriangleAlert } from "lucide-react";
import type { StockRow } from "../../api/cluckwork";
import type { ReactNode } from "react";
import { useFormat } from "../../farm/useFormat";
import { EmptyState } from "../../components/EmptyState";
import { StockBar } from "../../components/StockBar";
import { stockBar } from "../../lib/dashboard";
import { headingSx, sectionSx } from "./panelSx";

// The available-stock panel of the Dashboard (#1215): the total, the grade
// composition bar, the Grade/Count/Share ledger and the below-floor caption.
// It renders at exactly the page's old position in the panel grid; the
// below-floor rows and their fact string stay on the page and are passed in.
export function StockPanel({
  stock,
  stockFailed,
  panelError,
  t, fmt,
  belowFloorRows,
  belowFloorFact,
}: {
  stock: StockRow[] | null;
  stockFailed: boolean;
  panelError: ReactNode;
  t: ReturnType<typeof useTranslation<"dashboard">>["t"];
  fmt: ReturnType<typeof useFormat>;
  belowFloorRows: StockRow[];
  belowFloorFact: (key: "attentionGradeBelowFloor" | "stockCaptionBelowFloor") => string;
}) {
  const bar = stock === null ? null : stockBar(stock);
  // #911 — the ledger's mark. Named when one grade is short (the farm can act
  // on it directly), counted when several are.
  const belowFloorIds = new Set(belowFloorRows.map((r) => r.eggGradeId));
  return (
    <Card component="section" sx={{ ...sectionSx, gridColumn: { md: 2 }, gridRow: { md: 1 } }}>
      <Box sx={headingSx}>
        <Typography variant="h3" aria-label={t("stockPanelTitle")}><Link to="/stock">{t("availableStockTitle")}</Link></Typography>
      </Box>
      {bar === null || stock === null
        ? (stockFailed ? panelError
          : <LinearProgress aria-label={t("stockPanelTitle")} sx={{ height: 5, borderRadius: 2 }} />)
        : stock.length === 0 ? <EmptyState icon={Egg} message={t("noStockMessage")} /> : (
        <>
          <Typography className="stock-total" sx={{ display: "flex", alignItems: "baseline", justifyContent: "space-between", gap: 1, mb: 2,
            "& .stock-fig": { fontFamily: "Georgia, serif", fontSize: "2.5rem" } }}>
            <span className="stock-fig">{fmt.count(bar.totalAvailable)}</span>{" "}{t("eggsAvailableLabel", { count: bar.totalAvailable })}
          </Typography>
          <StockBar data={bar} />
          {bar.ledger.length > 0 && (
            <Table aria-label={t("stockLedgerLabel")} size="small" sx={{ mt: 1.5, tableLayout: "fixed", "& th, & td": { px: 0.75, py: 1, overflowWrap: "anywhere" } }}>
              <TableHead><TableRow>
                <TableCell sx={{ width: "46%" }}>{t("gradeColumn")}</TableCell>
                <TableCell align="right">{t("countColumn")}</TableCell>
                <TableCell align="right">{t("shareColumn")}</TableCell>
              </TableRow></TableHead>
              <TableBody>{bar.ledger.map((s) => (
                <TableRow key={s.eggGradeId} tabIndex={0} sx={{
                  "&:hover, &:focus": { bgcolor: "var(--surface-2)", outline: "2px solid var(--focus)", outlineOffset: -2 },
                  "&:hover th, &:focus th": { textDecoration: "underline", textUnderlineOffset: "3px" },
                }}>
                  <TableCell component="th" scope="row"><Box component="span" sx={{ display: "inline-flex", alignItems: "center", gap: 1 }}><span className={`swatch grade-${s.colorIndex}`} aria-hidden="true" />{s.gradeName}{belowFloorIds.has(s.eggGradeId) && (
                  /* #911 — a mark, not a tint: the icon carries the row's
                     own sentence as its accessible name. */
                  <Box component="span" role="img" aria-label={t("stockBelowFloorRowLabel", { grade: s.gradeName })} sx={{ display: "inline-flex", color: "var(--warn)" }}>
                    <TriangleAlert size={13} aria-hidden />
                  </Box>
                )}</Box></TableCell>
                  <TableCell align="right">{fmt.count(s.available)}</TableCell>
                  <TableCell align="right">{`${fmt.count(s.pct, 1)}%`}</TableCell>
                </TableRow>
              ))}</TableBody>
            </Table>
          )}
          {bar.totalRestricted > 0 && <Typography color="text.secondary" variant="caption" sx={{ display: "block", mt: 1 }}>{t("stockCaptionRestricted", { restricted: fmt.count(bar.totalRestricted) })}</Typography>}
          {belowFloorRows.length > 0 && <Typography variant="caption" sx={{
            display: "block", mt: 1, px: 1, py: 0.75, borderRadius: "var(--r-input)",
            bgcolor: "var(--tint-warn)", color: "var(--warn)", fontWeight: 650,
          }}>{belowFloorFact("stockCaptionBelowFloor")}</Typography>}
        </>
      )}
    </Card>
  );
}
