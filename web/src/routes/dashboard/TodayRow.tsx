import { Link } from "react-router";
import { useTranslation } from "react-i18next";
import { Box, Button, Tooltip, Typography } from "@mui/material";
import { Check, CircleDashed, TriangleAlert } from "lucide-react";
import type { CaptureTile } from "../../lib/dashboard";
import { useFormat } from "../../farm/useFormat";
import { StatusDot } from "./StatusDot";
import { statusLabel } from "../../i18n/enums";

export function TodayRow({ tile, today, fmt, t }: {
  tile: CaptureTile;
  today: string;
  fmt: ReturnType<typeof useFormat>;
  t: ReturnType<typeof useTranslation<"dashboard">>["t"];
}) {
  const { flock, entry } = tile;
  const href = `/daily-entry?flockId=${flock.id}&date=${today}`;
  const missing = entry === null;
  const draft = entry !== null && entry.status === "Draft";

  // Drafts use their last save; official entries use their submission time.
  // Older records without provenance retain the bare status label.
  const stateTime = missing
    ? null
    : fmt.time(draft ? (entry.lastChangedAtUtc ?? entry.createdAtUtc) : (entry.madeOfficialAtUtc ?? null));
  const stateLabel = missing || stateTime === null
    ? (missing ? undefined : statusLabel(entry.status))
    : t(draft ? "entryStateDraftTime" : "entryStateRecordedTime", { time: stateTime });

  return (
    <Box role="group" aria-label={flock.name} sx={{
      display: "grid", gridTemplateColumns: "28px minmax(0,1fr) auto", gap: 1.25,
      alignItems: "center", minHeight: 66, py: 1, borderTop: "1px solid var(--rule)",
    }}>
      <Box aria-hidden sx={{ display: "grid", placeItems: "center", width: 26, height: 26, borderRadius: "50%",
        color: missing ? "var(--warn)" : draft ? "var(--muted)" : "var(--success)",
        bgcolor: missing ? "var(--tint-warn)" : draft ? "var(--surface-2)" : "var(--tint-ok)",
      }}>{missing ? <TriangleAlert size={15} /> : draft ? <CircleDashed size={15} /> : <Check size={15} />}</Box>
      <Box sx={{ minWidth: 0, overflowWrap: "anywhere" }}>
        {/* describeChild: the hover hint describes the link, which already
            names itself via `aria-label` above (that wins over anything
            Tooltip would inject, describeChild or not) — this only adds the
            hint as a description rather than trying to replace the name. */}
        <Tooltip title={missing ? t("recordTodayHint") : undefined} describeChild>
          <Typography component={Link} to={href} sx={{ fontWeight: 600 }}
            aria-label={missing ? t("tileLinkLabelMissing", { flock: flock.name }) : t("tileLinkLabel", { flock: flock.name })}
          >{flock.name}</Typography>
        </Tooltip>
        <Box>{missing ? <StatusDot label={t("noEntryBadge")} forceColor="var(--warn)" /> : <StatusDot status={entry.status} label={stateLabel} />}</Box>
        {draft && <Typography component={Link} to={href} variant="body2">{t("continueHouseAction", { flock: flock.name })}</Typography>}
      </Box>
      {missing ? <Button component={Link} to={href} variant="outlined" color="inherit" size="small"
        aria-label={t("recordHouseAction", { flock: flock.name })}
        sx={{ gridColumn: "3", gridRow: "1", "&&": { minHeight: 44 } }}>{t("recordAction")}</Button> : <Box sx={{ textAlign: "right" }}>
        <Typography component="span" className="num" sx={{ fontFamily: "Georgia, serif", fontSize: "1.4rem", fontWeight: 600 }}>{fmt.count(entry.totalEggs)}</Typography>
      </Box>}
    </Box>
  );
}
