import { useCallback, useEffect, useRef, useState } from "react";
import { Trans, useTranslation } from "react-i18next";
import { Box, Typography } from "@mui/material";
import { getExpenseSummary, getProfitReport, getSalesSummary } from "../../api/cluckwork";
import type { ExpenseSummaryReport, ProfitReport, SalesSummary } from "../../api/cluckwork";
import { errText } from "../../lib/errText";
import { useFormat } from "../../farm/useFormat";
import { CONSOLE_PANEL_SX, CONSOLE_PAPER_HEAD_SX } from "../../components/FieldConsole";

// The Money tab (Owner and Manager only — the shell never mounts it for other
// roles, and the API refuses workers on these routes regardless). It requests
// ONLY its own three reports — never /reports/production, whose figures live
// on the Production tab (#1308).
export function MoneyTab({ from, to }: { from: string; to: string }) {
  const { t } = useTranslation("reports");
  const { t: tc } = useTranslation("common");
  const fmt = useFormat();
  const [sales, setSales] = useState<SalesSummary | null>(null);
  const [expenses, setExpenses] = useState<ExpenseSummaryReport | null>(null);
  const [profit, setProfit] = useState<ProfitReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  // Monotonic request id: a slow response for an OLD range must neither
  // overwrite the current range's figures nor clear its loading state
  // (codex review of #92).
  const requestSeq = useRef(0);

  const load = useCallback(async () => {
    const seq = ++requestSeq.current;
    setLoading(true);
    setError(null);
    // All three sections clear together — a partial failure must not leave the
    // new expenses beside the previous range's revenue.
    setSales(null);
    setExpenses(null);
    setProfit(null);
    try {
      // Sequential, not racing: each money card is one cheap aggregate.
      const s = await getSalesSummary(from, to);
      const e = await getExpenseSummary(from, to);
      const p = await getProfitReport(from, to);
      if (seq !== requestSeq.current) return;
      setSales(s);
      setExpenses(e);
      setProfit(p);
    } catch (err) {
      if (seq === requestSeq.current) setError(errText(err));
    } finally {
      if (seq === requestSeq.current) setLoading(false);
    }
  }, [from, to]);

  useEffect(() => { void load(); }, [load]);

  return (
    <>
      {error && (
        <p className="error" role="alert">
          {error}{" "}
          <button type="button" className="link" onClick={() => void load()}>
            {tc("retry")}
          </button>
        </p>
      )}
      {loading && <p className="muted">{tc("loading")}</p>}

      {sales && expenses && profit && (
        <Box component="section" aria-labelledby="reports-money-heading" sx={CONSOLE_PANEL_SX}>
          <Box component="header" sx={CONSOLE_PAPER_HEAD_SX}><h3 id="reports-money-heading">{t("moneyHeading")}</h3></Box>
          <Box component="dl" sx={{ m: 0 }}>
            <Box sx={{ display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto", gap: .5, py: 1, borderBottom: "1px solid var(--rule)" }}>
              <Typography component="dt" sx={{ fontWeight: 700 }}>{t("revenueRowLabel")}</Typography>
              <Typography component="dd" sx={{ m: 0, textAlign: "right", fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>{fmt.money(sales.revenueMinorUnits, sales.currencyCode, sales.currencyMinorUnit)}</Typography>
              <Typography component="dd" sx={{ m: 0, gridColumn: "1 / -1", fontSize: ".8rem", color: "text.secondary" }}>
                {t("salesSummary", {
                  count: sales.confirmedCount,
                  confirmed: fmt.count(sales.confirmedCount),
                  revenue: fmt.money(sales.revenueMinorUnits, sales.currencyCode, sales.currencyMinorUnit),
                  paid: fmt.money(sales.paidMinorUnits, sales.currencyCode, sales.currencyMinorUnit),
                  outstanding: fmt.money(sales.outstandingMinorUnits, sales.currencyCode, sales.currencyMinorUnit),
                })}
                {sales.voidedCount > 0 ? t("salesVoidedSuffix", { count: sales.voidedCount, voided: fmt.count(sales.voidedCount) }) : ""}
              </Typography>
            </Box>
            <Box sx={{ display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto", gap: .5, py: 1, borderBottom: "1px solid var(--rule)" }}>
              <Typography component="dt" sx={{ fontWeight: 700 }}>{t("eggsSoldRowLabel")}</Typography>
              <Typography component="dd" sx={{ m: 0, textAlign: "right", fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>{fmt.count(sales.eggsSold)}</Typography>
              {sales.eggsSoldByGrade.length > 0 && (
                <Typography component="dd" sx={{ m: 0, gridColumn: "1 / -1", fontSize: ".8rem", color: "text.secondary" }}>
                  {sales.eggsSoldByGrade.map((g) => `${g.name} ${fmt.count(g.quantity)}`).join(", ")}
                </Typography>
              )}
            </Box>
            <Box sx={{ display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto", gap: .5, py: 1, borderBottom: "1px solid var(--rule)" }}>
              <Typography component="dt" sx={{ fontWeight: 700 }}>{t("expensesRowLabel")}</Typography>
              <Typography component="dd" sx={{ m: 0, textAlign: "right", fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>{fmt.money(expenses.grandTotalMinorUnits, expenses.currencyCode, expenses.currencyMinorUnit)}</Typography>
              <Typography component="dd" sx={{ m: 0, gridColumn: "1 / -1", fontSize: ".8rem", color: "text.secondary" }}>
                {expenses.categories.length === 0
                  ? t("expensesNone")
                  : expenses.categories
                      .map((c) => `${c.name} ${fmt.money(c.totalMinorUnits, expenses.currencyCode, expenses.currencyMinorUnit)}`)
                      .join(", ")}
                {t("expensesTotalSuffix", {
                  total: fmt.money(expenses.grandTotalMinorUnits, expenses.currencyCode, expenses.currencyMinorUnit),
                })}
              </Typography>
            </Box>
            <Box sx={{ display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto", gap: .5, py: 1, borderBottom: "1px solid var(--rule)" }}>
              <Typography component="dt" sx={{ fontWeight: 700 }}>{t("feedCostPerDozenRowLabel")}</Typography>
              {/* Server figure in the report's currency; a range with no eggs has no rate (—), not 0 (#1298). */}
              <Typography component="dd" sx={{ m: 0, textAlign: "right", fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>
                {profit.feedCostPerDozenMinorUnits === null
                  ? "—"
                  : fmt.money(profit.feedCostPerDozenMinorUnits, profit.currencyCode, profit.currencyMinorUnit)}
              </Typography>
              {/* {{total}} is profit.feedCostMinorUnits, pre-formatted (DATA). */}
              <Typography component="dd" sx={{ m: 0, gridColumn: "1 / -1", fontSize: ".8rem", color: "text.secondary" }}>
                {t("feedCostLine", { total: fmt.money(profit.feedCostMinorUnits, profit.currencyCode, profit.currencyMinorUnit) })}
              </Typography>
            </Box>
            <Box sx={{ display: "grid", gridTemplateColumns: "minmax(0, 1fr) auto", gap: .5, py: 1, borderTop: "2px solid var(--ink)", fontWeight: 700 }}>
              <Typography component="dt" sx={{ fontWeight: 700 }}>{t("profitRowLabel")}</Typography>
              <Typography component="dd" sx={{ m: 0, textAlign: "right", fontWeight: 700, fontVariantNumeric: "tabular-nums" }}>{fmt.money(profit.profitMinorUnits, profit.currencyCode, profit.currencyMinorUnit)}</Typography>
              <Typography component="dd" sx={{ m: 0, gridColumn: "1 / -1", fontSize: ".8rem", color: "text.secondary" }}>
                <Trans ns="reports" i18nKey="profitLine"
                  values={{
                    revenue: fmt.money(profit.revenueMinorUnits, profit.currencyCode, profit.currencyMinorUnit),
                    expenses: fmt.money(profit.expensesMinorUnits, profit.currencyCode, profit.currencyMinorUnit),
                    profit: fmt.money(profit.profitMinorUnits, profit.currencyCode, profit.currencyMinorUnit),
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
