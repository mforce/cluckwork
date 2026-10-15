import { useTranslation } from "react-i18next";
import { Navigate, useNavigate, useParams, useSearchParams } from "react-router";
import { Box, Button, Tab, Tabs, Typography } from "@mui/material";
import { FilterBar, FilterDateField } from "../components/FilterBar";
import { FieldConsole } from "../components/FieldConsole";
import { daysBefore } from "../lib/dates";
import { useFarmToday } from "../farm/useFarm";
import { useAuth } from "../auth/useAuth";
import { MoneyTab } from "./reports/MoneyTab";
import { ProductionTab } from "./reports/ProductionTab";

// #91 — core reports. Production renders for everyone (workers record it,
// workers read it); Money is its own admin-only tab and the API refuses
// workers on those routes regardless.
export function ReportsPage() {
  const { t } = useTranslation("reports");
  const { t: tc } = useTranslation("common");
  const { isAdmin } = useAuth();
  // Farm-local, not browser-local: since #35 the API judges "is this date in
  // the future?" against the FARM's day, so the pickers must agree (#123).
  const today = useFarmToday();
  const { tab: urlTab } = useParams();
  const [searchParams, setSearchParams] = useSearchParams();
  const navigate = useNavigate();

  const from = searchParams.get("from") ?? daysBefore(today, 6);
  const to = searchParams.get("to") ?? today;
  const setRange = (nextFrom: string, nextTo: string) => {
    const next = new URLSearchParams(searchParams);
    next.set("from", nextFrom);
    next.set("to", nextTo);
    setSearchParams(next);
  };

  // A Worker or Sales user opening a /reports/money link lands on Production
  // with the range kept; Money never shows as an empty panel.
  if (urlTab !== "production" && (urlTab !== "money" || !isAdmin)) {
    return <Navigate to={`/reports/production?${searchParams.toString()}`} replace />;
  }

  const switchTab = (next: "production" | "money") => navigate(`/reports/${next}?${searchParams.toString()}`);

  return (
    <FieldConsole>
      <Typography variant="h2">{t("title")}</Typography>

      <FilterBar>
        <FilterDateField
          label={t("fromLabel")}
          value={from}
          slotProps={{ htmlInput: { max: to } }}
          onChange={(e) => setRange(e.target.value, to)}
        />
        <FilterDateField
          label={t("toLabel")}
          value={to}
          slotProps={{ htmlInput: { max: today } }}
          onChange={(e) => setRange(from, e.target.value)}
        />
        <Button variant="outlined" color="inherit" sx={{ borderRadius: "4px" }} onClick={() => setSearchParams({})}>{tc("clearFiltersButton")}</Button>
      </FilterBar>

      <Tabs value={urlTab}
        onChange={(_, value: "production" | "money") => switchTab(value)}
        aria-label={t("title")} sx={{ borderBottom: "1px solid var(--rule)", mb: 2 }}>
        <Tab id="reports-tab-production" aria-controls="reports-tabpanel-production" value="production" label={t("productionTab")} />
        {isAdmin && <Tab id="reports-tab-money" aria-controls="reports-tabpanel-money" value="money" label={t("moneyTab")} />}
      </Tabs>

      {urlTab === "production"
        ? <Box role="tabpanel" id="reports-tabpanel-production" aria-labelledby="reports-tab-production"><ProductionTab from={from} to={to} /></Box>
        : <Box role="tabpanel" id="reports-tabpanel-money" aria-labelledby="reports-tab-money"><MoneyTab from={from} to={to} /></Box>}
    </FieldConsole>
  );
}
