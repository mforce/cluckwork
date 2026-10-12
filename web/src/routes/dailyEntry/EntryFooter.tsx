import { useTranslation } from "react-i18next";
import { Box, Paper, Typography } from "@mui/material";
import type { Flock } from "../../api/cluckwork";
import { BusyButton } from "../../components/BusyButton";
import type { PickerSnapshot } from "../../components/NamedEntityPicker";
import type { DialogErrors } from "../../components/useDialogErrors";
import type { GradingTone } from "../../lib/grading";

interface EntryFooterProps {
  errors: DialogErrors;
  message: string | null;
  onSave: (submit: boolean) => Promise<void>;
  busy: boolean;
  isPending: (scope: string) => boolean;
  flockId: string;
  flockSnapshot: PickerSnapshot<Flock>;
  grading: { tone: GradingTone; count: number | null; says: string; short: string };
  entryLocked: boolean;
  prefillFailed: boolean;
  prefillPending: boolean;
}

// The sticky save/submit footer of the daily-entry screen.
export function EntryFooter({
  errors, message, onSave, busy, isPending, flockId, flockSnapshot, grading,
  entryLocked, prefillFailed, prefillPending,
}: EntryFooterProps) {
  const { t } = useTranslation(["dailyEntry", "dashboard"]);

// Save feedback lives with the saves: anything below a pinned bar
// scrolls underneath it and is never read. #830 — Paper elevation 4
// resolves to --shadow-bar via the theme's own shadow-index map (G2,
// #823: AppBar/Snackbar defaults 4/6 -> --shadow-bar), so this needs no
// new styles.css rule and no new G2 row; `.entry-foot`'s own box-shadow
// declaration is deleted below instead of replaced.
// Side by side (#823's `.entry-foot .actions` exception, #740): the
// confirmed mockup keeps Save draft and Submit day side by side at
// 390, not stacked like every other `.actions` row — see the PR body's
// D3.3/D8 amendment.
  return (
    <Paper component="footer" elevation={4} square sx={{
      position: "sticky", bottom: "var(--tabbar-h)", zIndex: 10,
      mt: 4, mx: { xs: -2, md: 0 }, px: { xs: 2, md: 0 },
      pt: 1.5, pb: "calc(12px + env(safe-area-inset-bottom))",
      borderTop: "1px solid var(--hairline)",
    }}
    >
      {/* #479 — unconditional: the new-flock dialog's own failures live in
          their own slot now (see DialogError above), so there is nothing
          here for a dialog message to double up on. */}
      {errors.page && <Typography role="alert" className="error">{errors.page}</Typography>}
      {message && <Typography role="status" className="success">{message}</Typography>}
      {/* `entry-actions` carries no styles.css rule — sx owns every visual
          here — it exists only so phone.spec.ts can measure this row
          without measuring the footer's own outer padding (was
          `.entry-foot .actions`, a real styled class; #830 kept the hook,
          dropped the styling it used to carry). */}
      <Box className="entry-actions" sx={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 1.5 }}>
        {/* Sibling triggers: each spins only for its own scope, while the
            shared `busy` in disabled keeps the other one inert.
            `grading.tone === "over"` (not the narrower `lossesExceedTotal`)
            because #443 made an over-graded draft reachable a second way:
            setGrade only ever RAISES the total to fit a grade, so the one
            path still left to "over" is trimming the total on step 1 below
            a sum already graded — that must still block the save the
            lenient backend rule would reject anyway (#394), rather than
            round-trip to find out. `tone === "over"` already covers the
            lossesExceedTotal case too (see lib/grading). */}
        {/* #830 (owner's screenshot review of #888) — the confirmed mockup's
            footer is two 48px rectangular buttons, outlined "Save draft" /
            contained "Submit day", never the stylesheet's pill `<button>`
            (which clamped "Save & submit (creates egg lots)" into a
            three-line ellipse at 390, the #740 shape).
            `whiteSpace: "nowrap"` is the other half: MUI's default button
            text wraps, and a wrapped label inside a fixed-height control
            clips instead of growing the ellipse this control no longer has.
            `color`/`borderColor` are explicit: MUI's outlined default reads
            `palette.primary.main` (`--brand`), which dark mode never
            redefines (DIRECTION.md reserves brand for the farm name, the
            active nav item, the PRIMARY button and the focus ring — never a
            secondary outline) — a dark-aubergine border/text on the near-
            black dark canvas measured as low-contrast (Playwright capture,
            #830). `var(--ink)`/`var(--rule-strong)` match the mockup's
            `.btn.secondary` instead, and both tokens ARE redefined for dark
            mode. */}
        <BusyButton variant="outlined"
          busy={isPending("save")}
          sx={{
            minHeight: 48, whiteSpace: "nowrap",
            color: "var(--ink)", borderColor: "var(--rule-strong)",
            "&:hover": { borderColor: "var(--ink)" },
          }}
          disabled={busy || !flockId || !flockSnapshot.canSubmit || grading.tone === "over" || entryLocked || prefillFailed || prefillPending}
          onClick={() => onSave(false)}>{t("saveDraftButton")}</BusyButton>
        {/* #394: submit requires grading to reconcile EXACTLY — the same
            "done" state the chip already shows, so the gate can never say
            one thing and disable another. A draft may stay partially (or
            entirely un-)graded; only submit is gated. */}
        <BusyButton variant="contained"
          busy={isPending("submit")}
          sx={{ minHeight: 48, whiteSpace: "nowrap" }}
          disabled={busy || !flockId || !flockSnapshot.canSubmit || grading.tone !== "done" || entryLocked || prefillFailed || prefillPending}
          onClick={() => onSave(true)}>
          {t("submitButton")}
        </BusyButton>
      </Box>
    </Paper>
  );
}
