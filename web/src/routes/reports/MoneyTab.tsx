import { Trans, useTranslation } from "react-i18next";
import { Box, Typography } from "@mui/material";
import { getExpenseSummary, getProfitReport, getSalesSummary } from "../../api/cluckwork";
import type { ExpenseSummaryReport, ProfitReport, SalesSummary } from "../../api/cluckwork";
import { errText } from "../../lib/errText";
import { useFormat } from "../../farm/useFormat";
import { CONSOLE_PANEL_SX, CONSOLE_PAPER_HEAD_SX } from "../../components/FieldConsole";
import { useLatestLoad } from "../../components/useLatestLoad";
import { inclusiveDays } from "../../lib/dates";
import { usePreviousPeriod } from "./periodComparison";
import { PeriodChange } from "./PeriodChange";

interface MoneyReports {
  sales: SalesSummary;
  expenses: ExpenseSummaryReport;
  profit: ProfitReport;
}

async function readMoney(from: string, to: string, signal: AbortSignal): Promise<MoneyReports> {
  const sales = await getSalesSummary(from, to, signal);
  signal.throwIfAborted();
  const expenses = await getExpenseSummary(from, to, signal);
  signal.throwIfAborted();
  const profit = await getProfitReport(from, to, signal);
  return { sales, expenses, profit };
}

// Only Owner and Manager mount this tab; the API refuses other roles on these
// routes regardless.
export function MoneyTab({ from, to }: { from: string; to: string }) {
  const { t } = useTranslation("reports");
  const { t: tc } = useTranslation("common");
  const fmt = useFormat();
  const { data, error, loading, retry } = useLatestLoad([from, to],
    (signal) => readMoney(from, to, signal));
  const reports = loading ? null : data;
  const previous = usePreviousPeriod(from, to, reports, readMoney);
  const days = inclusiveDays(from, to);

  return (
    <>
      {error !== null && (
        <p className="error" role="alert">
          {errText(error)}{" "}
          <button type="button" className="link" onClick={retry}>
            {tc("retry")}
          </button>
        </p>
      )}
      {loading && <p className="muted">{tc("loading")}</p>}

      {reports && (
        <Box component="section" aria-labelledby="reports-money-heading" sx={CONSOLE_PANEL_SX}>
          <Box component="header" sx={CONSOLE_PAPER_HEAD_SX}><h3 id="reports-money-heading">{t("moneyHeading")}</h3></Box>
          <Box component="dl" sx={{ m: 0 }}>
            <Box sx={{ display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto", gap: .5, py: 1, borderBottom: "1px solid var(--rule)" }}>
              <Typography component="dt" sx={{ fontWeight: 700 }}>{t("revenueRowLabel")}</Typography>
              <Typography component="dd" sx={{ m: 0, textAlign: "right", fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>{fmt.money(reports.sales.revenueMinorUnits, reports.sales.currencyCode, reports.sales.currencyMinorUnit)}<PeriodChange current={reports.sales.revenueMinorUnits} previous={previous?.sales.revenueMinorUnits} days={days} /></Typography>
              <Typography component="dd" sx={{ m: 0, gridColumn: "1 / -1", fontSize: ".8rem", color: "text.secondary" }}>
                {t("salesSummary", {
                  count: reports.sales.confirmedCount,
                  confirmed: fmt.count(reports.sales.confirmedCount),
                  revenue: fmt.money(reports.sales.revenueMinorUnits, reports.sales.currencyCode, reports.sales.currencyMinorUnit),
                  paid: fmt.money(reports.sales.paidMinorUnits, reports.sales.currencyCode, reports.sales.currencyMinorUnit),
                  outstanding: fmt.money(reports.sales.outstandingMinorUnits, reports.sales.currencyCode, reports.sales.currencyMinorUnit),
                })}
                {reports.sales.voidedCount > 0 ? t("salesVoidedSuffix", { count: reports.sales.voidedCount, voided: fmt.count(reports.sales.voidedCount) }) : ""}
              </Typography>
            </Box>
            <Box sx={{ display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto", gap: .5, py: 1, borderBottom: "1px solid var(--rule)" }}>
              <Typography component="dt" sx={{ fontWeight: 700 }}>{t("eggsSoldRowLabel")}</Typography>
              <Typography component="dd" sx={{ m: 0, textAlign: "right", fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>{fmt.count(reports.sales.eggsSold)}<PeriodChange current={reports.sales.eggsSold} previous={previous?.sales.eggsSold} days={days} /></Typography>
              {reports.sales.eggsSoldByGrade.length > 0 && (
                <Typography component="dd" sx={{ m: 0, gridColumn: "1 / -1", fontSize: ".8rem", color: "text.secondary" }}>
                  {reports.sales.eggsSoldByGrade.map((g) => `${g.name} ${fmt.count(g.quantity)}`).join(", ")}
                </Typography>
              )}
            </Box>
            <Box sx={{ display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto", gap: .5, py: 1, borderBottom: "1px solid var(--rule)" }}>
              <Typography component="dt" sx={{ fontWeight: 700 }}>{t("expensesRowLabel")}</Typography>
              <Typography component="dd" sx={{ m: 0, textAlign: "right", fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>{fmt.money(reports.expenses.grandTotalMinorUnits, reports.expenses.currencyCode, reports.expenses.currencyMinorUnit)}<PeriodChange current={reports.expenses.grandTotalMinorUnits} previous={previous?.expenses.grandTotalMinorUnits} days={days} /></Typography>
              <Typography component="dd" sx={{ m: 0, gridColumn: "1 / -1", fontSize: ".8rem", color: "text.secondary" }}>
                {reports.expenses.categories.length === 0
                  ? t("expensesNone")
                  : reports.expenses.categories
                      .map((c) => `${c.name} ${fmt.money(c.totalMinorUnits, reports.expenses.currencyCode, reports.expenses.currencyMinorUnit)}`)
                      .join(", ")}
                {t("expensesTotalSuffix", {
                  total: fmt.money(reports.expenses.grandTotalMinorUnits, reports.expenses.currencyCode, reports.expenses.currencyMinorUnit),
                })}
              </Typography>
            </Box>
            <Box sx={{ display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto", gap: .5, py: 1, borderBottom: "1px solid var(--rule)" }}>
              <Typography component="dt" sx={{ fontWeight: 700 }}>{t("feedCostPerDozenRowLabel")}</Typography>
              {/* Server figure in the report's currency; a range with no eggs has no rate (—), not 0 (#1298). */}
              <Typography component="dd" sx={{ m: 0, textAlign: "right", fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>
                {reports.profit.feedCostPerDozenMinorUnits === null
                  ? "—"
                  : fmt.money(reports.profit.feedCostPerDozenMinorUnits, reports.profit.currencyCode, reports.profit.currencyMinorUnit)}
                <PeriodChange current={reports.profit.feedCostPerDozenMinorUnits} previous={previous?.profit.feedCostPerDozenMinorUnits} days={days} />
              </Typography>
              {/* {{total}} is profit.feedCostMinorUnits, pre-formatted (DATA). */}
              <Typography component="dd" sx={{ m: 0, gridColumn: "1 / -1", fontSize: ".8rem", color: "text.secondary" }}>
                {t("feedCostLine", { total: fmt.money(reports.profit.feedCostMinorUnits, reports.profit.currencyCode, reports.profit.currencyMinorUnit) })}
              </Typography>
            </Box>
            <Box sx={{ display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto", gap: .5, py: 1, borderTop: "2px solid var(--ink)", fontWeight: 700 }}>
              <Typography component="dt" sx={{ fontWeight: 700 }}>{t("profitRowLabel")}</Typography>
              <Typography component="dd" sx={{ m: 0, textAlign: "right", fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>{fmt.money(reports.profit.profitMinorUnits, reports.profit.currencyCode, reports.profit.currencyMinorUnit)}<PeriodChange current={reports.profit.profitMinorUnits} previous={previous?.profit.profitMinorUnits} days={days} /></Typography>
              <Typography component="dd" sx={{ m: 0, gridColumn: "1 / -1", fontSize: ".8rem", color: "text.secondary" }}>
                <Trans ns="reports" i18nKey="profitLine"
                  values={{
                    revenue: fmt.money(reports.profit.revenueMinorUnits, reports.profit.currencyCode, reports.profit.currencyMinorUnit),
                    expenses: fmt.money(reports.profit.expensesMinorUnits, reports.profit.currencyCode, reports.profit.currencyMinorUnit),
                    profit: fmt.money(reports.profit.profitMinorUnits, reports.profit.currencyCode, reports.profit.currencyMinorUnit),
                  }}
                  components={{ strong: <strong /> }}
                />
              </Typography>
            </Box>
          </Box>
          <p className="muted">
            {t("profitFootnote")}
          </p>
        </Box>
      )}
    </>
  );
}
