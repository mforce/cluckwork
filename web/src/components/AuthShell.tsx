import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Box, Paper, Typography } from "@mui/material";
import { ThemeToggle } from "./ThemeToggle";

// #833 — the two-pane "Working desk" shell (Concept B,
// docs/designs/674-tail-redesign/) shared by Login and SetPasswordPage: a
// fixed brand panel on the left (the farm's own --brand colour, stable
// across light/dark since --brand is never redeclared in the dark palette
// blocks — verified in styles.css), and the screen's own form on the right.
// At phone width the two panes stack instead of sitting side by side.
export function AuthShell({
  children, footerNote, bannerSlot,
}: { children: ReactNode; footerNote: string; bannerSlot?: ReactNode }) {
  const { t } = useTranslation("auth");

  return (
    <Box
      component="main"
      sx={{
        position: "relative", minHeight: "100dvh", display: "flex",
        alignItems: { xs: "stretch", md: "center" }, justifyContent: "center",
        // Same D3.3 rule the auth shell always had: the gradient stays at
        // 1280, the phone layout is flat and full-width with no bleed.
        background: { xs: "var(--canvas)", md: "var(--auth-bg)" },
        padding: { xs: 0, md: 3 },
      }}
    >
      <Box sx={{ position: "absolute", top: "1.1rem", right: "1.1rem", zIndex: 1 }}>
        <ThemeToggle showLabel={false} iconSize={18} />
      </Box>
      <Paper
        elevation={0}
        sx={{
          display: "flex", flexDirection: { xs: "column", md: "row" },
          width: { xs: "100%", md: "min(920px, 100%)" },
          minHeight: { xs: "100dvh", md: "auto" },
          border: { md: "1px solid var(--auth-card-border)" },
          boxShadow: { md: "var(--auth-card-shadow)" },
          overflow: "hidden",
        }}
      >
        <Box
          sx={{
            width: { md: "42%" }, backgroundColor: "var(--brand)", color: "var(--on-brand)",
            padding: { xs: 3, md: 4 }, display: "flex", flexDirection: "column",
          }}
        >
          <Typography variant="overline" sx={{ color: "var(--on-brand-mute)" }}>
            {t("shellEyebrow")}
          </Typography>
          <Typography variant="h1" sx={{ color: "var(--on-brand)" }}>{t("title")}</Typography>
          <Typography variant="body2" sx={{ color: "var(--on-brand-mute)" }}>
            {t("shellTagline")}
          </Typography>
          {bannerSlot}
          <Typography variant="caption" sx={{ color: "var(--on-brand-mute)", marginTop: "auto", paddingTop: 3 }}>
            {footerNote}
          </Typography>
        </Box>
        <Box sx={{ flex: 1, padding: { xs: 3, md: 4 }, backgroundColor: "background.paper" }}>
          {children}
        </Box>
      </Paper>
    </Box>
  );
}
