import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Box, Paper, Typography } from "@mui/material";
import { ThemeToggle } from "./ThemeToggle";

export function AuthShell({
  children, footerNote, bannerSlot,
}: { children: ReactNode; footerNote: string; bannerSlot?: ReactNode }) {
  const { t } = useTranslation("auth");

  return (
    <Box
      component="main"
      sx={{
        position: "relative", minHeight: "100dvh", display: "flex",
        alignItems: { xs: "flex-start", md: "center" }, justifyContent: "center",
        background: "var(--surface-2)",
        padding: { xs: "66px 14px 22px", md: 3.5 },
      }}
    >
      <Box sx={{ position: "absolute", top: "1.1rem", right: "1.1rem", zIndex: 1 }}>
        <ThemeToggle showLabel={false} iconSize={18} />
      </Box>
      <Paper
        elevation={0}
        sx={{
          display: "flex", flexDirection: { xs: "column", md: "row" },
          width: { xs: "100%", md: "min(880px, 100%)" },
          border: "1px solid var(--hairline)", borderRadius: 0,
          boxShadow: "none",
          overflow: "hidden",
        }}
      >
        <Box
          sx={{
            width: { md: "50%" }, backgroundColor: "var(--brand)", color: "var(--on-brand)",
            padding: { xs: "20px", md: "38px" }, display: "flex", flexDirection: "column",
          }}
        >
          <Typography variant="overline" sx={{ color: "var(--on-brand-mute)" }}>
            {t("shellEyebrow")}
          </Typography>
          <Typography variant="h1" sx={{
            color: "var(--on-brand)", fontSize: { xs: "1.75rem", md: "2.5rem" },
          }}>{t("title")}</Typography>
          <Typography variant="body2" sx={{ color: "var(--on-brand-mute)", display: { xs: "none", md: "block" } }}>
            {t("shellTagline")}
          </Typography>
          {bannerSlot}
          <Typography variant="caption" sx={{ color: "var(--on-brand-mute)", marginTop: "auto", paddingTop: 3 }}>
            {footerNote}
          </Typography>
        </Box>
        <Box sx={{ flex: 1, padding: { xs: "20px", md: "32px" }, backgroundColor: "background.paper" }}>
          {children}
        </Box>
      </Paper>
    </Box>
  );
}
