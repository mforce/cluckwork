import { Trans, useTranslation } from "react-i18next";
import { Link } from "react-router";
import { Box, Button, Table, TableBody, TableCell, TableHead, TableRow, Tooltip, useMediaQuery } from "@mui/material";
import type { DailyEntry } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import { FarmDate } from "../../components/FarmDate";
import { CONSOLE_LINK_SX, LedgerTableContainer } from "../../components/FieldConsole";
import { GlossaryLink } from "../../components/GlossaryLink";
import { PhoneLedgerList, PhoneLedgerRow } from "../../components/PhoneLedger";
import { ProvenanceCell } from "../../components/ProvenanceCell";
import { useFormat } from "../../farm/useFormat";
import { MD_UP_QUERY } from "../../lib/breakpoints";
import type { PagedEntries } from "./historyUi";

const NOWRAP = { whiteSpace: "nowrap" as const };

// #980 — below the 900px switch the ten-column ledger becomes one two-line
// button per entry, so twelve rows fit a 390x844 screen instead of five and
// the actions stop living off the right edge. The desktop table is untouched.

export function HistoryLedgers({
  entries, rowActions, rowFlockName, conditionStock, gradeList, busy, isPending, startAdjust, onVoid, setDetails,
}: {
  entries: PagedEntries;
  rowFlockName: (e: { flockId: string; flockName?: string | null }) => string;
  conditionStock: (e: DailyEntry) => string;
  gradeList: (e: DailyEntry) => string;
  rowActions: (e: DailyEntry) => { viewHistory: boolean; edit: boolean; correct: boolean };
  busy: boolean;
  isPending: (scope: string) => boolean;
  startAdjust: (e: DailyEntry) => void;
  onVoid: (e: DailyEntry) => Promise<void>;
  setDetails: (details: DailyEntry | null) => void;
}) {
  const { t } = useTranslation("history");
  const { t: tc } = useTranslation("common");
  const fmt = useFormat();
  const isDesktop = useMediaQuery(MD_UP_QUERY);

  // Unreachable: the page renders the ledgers only when rows are loaded.
  if (entries.rows === null) return null;

  const lossList = (e: DailyEntry) =>
    `${fmt.count(e.crackedEggs)}/${fmt.count(e.dirtyEggs)}/${fmt.count(e.discardedEggs)}`;

  function statusCell(e: DailyEntry) {
    const states: Record<string, { label: string; color: string; title?: string }> = {
      Voided: { label: t("statusVoided"), color: "var(--error)", title: e.voidReason ?? undefined },
      ManagerAdjusted: { label: t("statusAdjusted"), color: "var(--warn)", title: e.adjustReason ?? undefined },
      Locked: { label: t("statusLocked"), color: "var(--link)", title: e.lockedAtUtc ? t("lockedAt", { time: e.lockedAtUtc }) : undefined },
      Submitted: { label: t("statusSubmitted"), color: "var(--success)" },
    };
    const state = states[e.status] ?? { label: t("statusDraft"), color: "var(--muted)" };
    return (
      // describeChild: the reason/timestamp DESCRIBES the pill, it is not the
      // pill's accessible NAME — without it Tooltip's default replaces the
      // pill's name (the label text below) with the title text instead.
      <Tooltip title={state.title} describeChild>
        <Box component="span" sx={{ display: "inline-flex", alignItems: "center", gap: "5px", fontWeight: 700, whiteSpace: "nowrap" }}>
          <Box component="span" aria-hidden="true" sx={{ width: "6px", height: "6px", borderRadius: "50%", bgcolor: state.color, flexShrink: 0 }} />
          {state.label}
        </Box>
      </Tooltip>
    );
  }

  return (
    <>
      {isDesktop ? (
        <LedgerTableContainer>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>{t("dateHeader")}</TableCell>
                <TableCell>{t("flockHeader")}</TableCell>
                <TableCell>{t("statusHeader")}<GlossaryLink term="LockedEntry" /></TableCell>
                <TableCell align="right">{t("totalHeader")}</TableCell>
                <TableCell align="right">{t("lossesHeader")}</TableCell>
                {/* Condition counts only losses added to stock; Losses includes all recorded losses. */}
                <TableCell align="right">{t("conditionHeader")}</TableCell>
                <TableCell align="right">{t("mortalityHeader")}</TableCell>
                <TableCell>{t("gradedHeader")}</TableCell>
                <TableCell>{tc("recordHistoryHeader")}</TableCell>
                <TableCell></TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {entries.rows.map((e) => (
                <TableRow key={e.id}
                  // MUI cell colours override inherited row colours.
                  sx={e.status === "Voided" ? { "& .MuiTableCell-root": { color: "var(--muted)" } } : undefined}>
                  <TableCell sx={NOWRAP}><FarmDate iso={e.date} /></TableCell>
                  <TableCell>{rowFlockName(e)}</TableCell>
                  <TableCell>{statusCell(e)}</TableCell>
                  <TableCell align="right">{fmt.count(e.totalEggs)}</TableCell>
                  <TableCell align="right">{lossList(e)}</TableCell>
                  <TableCell align="right">{conditionStock(e)}</TableCell>
                  <TableCell align="right">{fmt.count(e.mortalityCount)}</TableCell>
                  <TableCell>{gradeList(e)}</TableCell>
                  <ProvenanceCell history={e} official="submitted" />
                  <TableCell sx={NOWRAP}>
                    {/* The audit endpoint is AdminOnly, even when this ledger is readable by workers. */}
                    {rowActions(e).viewHistory && (
                      <Link className="link" to={`/audit?entityId=${e.id}`}>
                        {tc("recordHistory.viewHistoryLink")}
                      </Link>
                    )}
                    {rowActions(e).edit && (
                      <Button component={Link} size="small" sx={{ ...CONSOLE_LINK_SX, ml: 1 }}
                        to={`/daily-entry?flockId=${e.flockId}&date=${e.date}`}>
                        {t("editButton")}
                      </Button>
                    )}
                    {rowActions(e).correct && (
                      <>
                        <Button size="small" sx={{ ...CONSOLE_LINK_SX, ml: 1 }} disabled={busy}
                          onClick={() => startAdjust(e)}>{t("adjustButton")}</Button>
                        <BusyButton variant="text" size="small" sx={{ ...CONSOLE_LINK_SX, color: "var(--error)", ml: 1 }} busy={isPending(`void:${e.id}`)}
                          disabled={busy}
                          onClick={() => void onVoid(e)}>{t("voidButton")}</BusyButton>
                      </>
                    )}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </LedgerTableContainer>
      ) : (
        // #980 — one two-line button per entry. The whole row is the tap
        // target and opens Details; the mockups' ⋯ menu was dropped by the
        // owner, so there is no second control competing for the thumb.
        <PhoneLedgerList label={t("title")}>
          {entries.rows.map((e) => (
            <li key={e.id}>
              <PhoneLedgerRow onClick={() => setDetails(e)} muted={e.status === "Voided"}
                date={<FarmDate iso={e.date} />} primary={rowFlockName(e)} trailing={statusCell(e)}
                summary={
                  <Trans ns="history" i18nKey="rowSummary" components={{ strong: <strong /> }}
                    count={e.mortalityCount}
                    values={{ total: fmt.count(e.totalEggs), losses: lossList(e), deaths: fmt.count(e.mortalityCount) }} />
                } />
            </li>
          ))}
        </PhoneLedgerList>
      )}
      {entries.canLoadMore && (
        <button className="link" disabled={busy}
          onClick={() => void entries.loadMore()}>
          {t("loadMoreButton")}
        </button>
      )}
    </>
  );
}
