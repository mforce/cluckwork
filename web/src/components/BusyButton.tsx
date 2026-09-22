import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Box, Button, CircularProgress } from "@mui/material";
import type { ButtonProps } from "@mui/material";

type Props = {
  busy?: boolean;
  children?: ReactNode;
} & Omit<ButtonProps, "children">;

// #236 — the busy trigger, always MUI's own `Button`: every caller states its
// own `variant`/`color`, the same as any other `Button` in this app.
// Children pass through untouched (dynamic labels like Login's "Signing in…"
// swap stay the caller's); the wrapper's inline-flex gap is what seats the
// spinner beside them. The ring sits INLINE before the label at full
// brightness — an earlier overlay version dimmed the label and stacked the
// ring on top, and read as barely-there on the terracotta buttons (owner
// call, 2026-07-28). The button widening slightly while busy is the accepted
// cost; it is disabled for the duration, so nothing under the cursor is
// clickable anyway.
//
// The live region is a SIBLING of the button, not a child: aria-busy tells AT
// to defer announcing changes inside the busy element, so a region in there
// may never speak. It stays MOUNTED with its text swapped — a region that
// mounts already populated is unreliably announced (same pattern as the
// Settings logo status). It is .sr-only (absolute-positioned), so the
// fragment adds no layout. The spinner is aria-hidden so the accessible name
// stays exactly the children text — screen tests assert names verbatim.
export function BusyButton({ busy = false, disabled, children, ...rest }: Props) {
  const { t } = useTranslation("common");
  return (
    <>
      <Button {...rest} disabled={disabled || busy} aria-busy={busy || undefined}>
        <Box component="span" className="busy-label" sx={{ display: "inline-flex", alignItems: "center", gap: "0.4rem" }}>
          {busy && (
            <CircularProgress
              className="spinner"
              size={16}
              thickness={5}
              aria-hidden="true"
              sx={{ color: "currentColor", flexShrink: 0 }}
            />
          )}
          {children}
        </Box>
      </Button>
      <span role="status" className="sr-only">
        {busy ? t("working") : ""}
      </span>
    </>
  );
}
