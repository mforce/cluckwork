import { useTranslation } from "react-i18next";
import { Box, Typography, useMediaQuery } from "@mui/material";
import type { Flock } from "../../api/cluckwork";
import { FarmDate } from "../../components/FarmDate";
import { useFormat } from "../../farm/useFormat";
import { useFarm, useFarmToday } from "../../farm/useFarm";
import { MD_UP_QUERY } from "../../lib/breakpoints";

interface EntryHeaderProps {
  selectedFlock: Flock | undefined;
  existingStatus: string | null;
  prefillPending: boolean;
  prefillFailed: boolean;
  existingSavedAt: string | null;
  mortality: number;
  entryLocked: boolean;
}

// The phone top bar, the title/status line and the attention line of the
// daily-entry screen. `editingDraft`, `savedTime` and the attention items are
// derived here exactly as the page derived them before the move.
export function EntryHeader({
  selectedFlock, existingStatus, prefillPending, prefillFailed,
  existingSavedAt, mortality, entryLocked,
}: EntryHeaderProps) {
  const { t } = useTranslation(["dailyEntry", "dashboard"]);
  const fmt = useFormat();
  const today = useFarmToday();
  const { farm } = useFarm();

  // The prefill found a draft for this flock+date: the form is EDITING it,
  // not starting fresh, and nothing said so before (#134).
  //
  // Gated on a SETTLED prefill. existingStatus still holds the previous
  // flock+date's value while a new one is in flight, and is never cleared if
  // that fetch fails — so without this the badge claims "editing draft" for a
  // day it knows nothing about, and keeps claiming it (codex review).
  const editingDraft = existingStatus === "Draft" && !prefillPending && !prefillFailed;

  // #830 — farm-local, formatted once; null when the prefetched record has
  // neither `lastChangedAtUtc` nor `createdAtUtc` (pre-#494 rows only).
  const savedTime = fmt.time(existingSavedAt);

  // #830 — the attention line (mockup: daily-entry.html `.attn`). Same fold
  // rule as Dashboard's (#883 round 2, finding 1): never wraps, two items fit
  // at 1280 and one at 390, the same md (900px) boundary the sidebar/tab-bar
  // switch uses.
  const isDesktop = useMediaQuery(MD_UP_QUERY);
  const attentionCap = isDesktop ? 2 : 1;
  const attentionItems: string[] = [];
  if (editingDraft) attentionItems.push(t("attentionDraftItem"));
  // Read-only projection of what submitting would do to the flock's bird
  // ledger — not a write, and not the write's own guard (FR-009 stays on the
  // handler). Only while the mortality this DAY would still apply on submit.
  //
  // `!prefillPending && !prefillFailed` (CodeRabbit review of #888, ff274b0):
  // `selectedFlock` is derived and updates the instant `flockId` changes, but
  // `mortality` is state that only catches up once the prefill for the NEW
  // flock+date resolves — so retargeting without this guard kept projecting
  // the OLD target's mortality count against the NEW flock's `currentBirds`
  // for the whole prefill window, not just its first render. `canAssign` and
  // `editingDraft` above already carry this same guard for the same reason.
  if (!entryLocked && mortality > 0 && selectedFlock && !prefillPending && !prefillFailed) {
    attentionItems.push(t("attentionMortalityItem", {
      count: mortality,
      flockCountAfter: fmt.count(Math.max(0, selectedFlock.currentBirds - mortality)),
    }));
  }
  const attentionShown = attentionItems.slice(0, attentionCap);
  const attentionMoreCount = attentionItems.length - attentionShown.length;

  return (
    <>
      {/* #830 — phone-only top bar (mockup: daily-entry.html `.topbar`), farm
          name left, today's date right. No shared shell equivalent exists yet
          — AppLayout/BottomNav render no such bar for any screen — so this
          stays page-local rather than assuming shell support that has not
          shipped; a later slice can promote it once a second screen wants it. */}
      <Box sx={{
        display: { xs: "flex", md: "none" }, alignItems: "center",
        justifyContent: "space-between", color: "var(--stat-accent)",
        fontWeight: 600, mb: 2,
      }}
      >
        <Typography component="span" sx={{ fontWeight: 600 }}>{farm?.name ?? "Cluckwork"}</Typography>
        <Typography component="span" variant="caption" sx={{ fontWeight: 400 }}><FarmDate iso={today} /></Typography>
      </Box>

      <Box sx={{ display: "flex", flexDirection: "column", gap: 0.5 }}>
        <Typography variant="h2">{t("title")}</Typography>
        {/* Always rendered so it is a live region BEFORE the prefill fills it;
            a status container that appears at the same moment as its content is
            unreliably announced. #830 — "Draft, saved 06:52" when the prefill's
            own RecordHistory timestamp is available (every real record has
            one; #819), else the bare "Editing draft" fallback a pre-#494 row
            with no history still gets. */}
        <Typography component="span" role="status" className="muted" sx={{ display: "flex", flexWrap: "wrap", gap: 0.75 }}>
          {selectedFlock && <span>{selectedFlock.name}</span>}
          {selectedFlock && editingDraft && <span aria-hidden>·</span>}
          {editingDraft && (
            <span>
              {savedTime !== null
                ? t("dashboard:entryStateDraftTime", { time: savedTime })
                : t("editingDraftBadge")}
            </span>
          )}
        </Typography>
      </Box>

      {/* #830 — the attention line (mockup: daily-entry.html `.attn`), the
          same ruled-separator/fold-to-"+N more" treatment Dashboard's #829/
          #864 attention line already carries — see routes/dashboard/
          MorningBrief.tsx for the precedent this mirrors. */}
      {attentionShown.length > 0 && (
        <Box sx={{
          display: "flex", alignItems: "center", gap: 1.5, mt: 1.5, px: 1.5, py: 1,
          minHeight: 40, overflow: "hidden", whiteSpace: "nowrap",
          backgroundColor: "var(--tint-warn)", border: "1px solid var(--rule)",
          borderRadius: "var(--r-input)",
        }}
        >
          <Box component="span" aria-hidden sx={{ width: 8, height: 8, borderRadius: "50%", bgcolor: "var(--warn)", flexShrink: 0 }} />
          <Typography component="span" sx={{ fontWeight: 600 }}>
            {t("attentionHeading", { count: attentionItems.length })}
          </Typography>
          {attentionShown.map((item, i) => (
            <Box key={i} sx={{ display: "flex", alignItems: "center", gap: 1.5 }}>
              <Box component="span" aria-hidden sx={{ width: "1px", height: 12, bgcolor: "var(--rule-strong)" }} />
              <Typography component="span" variant="body1">{item}</Typography>
            </Box>
          ))}
          {attentionMoreCount > 0 && (
            <Typography component="span" variant="body1">
              {t("dashboard:attentionMore", { count: attentionMoreCount })}
            </Typography>
          )}
        </Box>
      )}
    </>
  );
}
