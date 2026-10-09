import { useEffect } from "react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Box, Paper, Typography } from "@mui/material";
import { ThemeToggle } from "./ThemeToggle";
import { resyncThemeColorMeta } from "../theme/metaThemeColor";

// nextStep (#798, login B) names what follows sign-in: in the brand panel in place
// of the tagline on a wide screen, and above the form on a phone, where the
// panel is a short band.
export function AuthShell({
  children, footerNote, bannerSlot, nextStep,
}: { children: ReactNode; footerNote: string; bannerSlot?: ReactNode; nextStep?: ReactNode }) {
  const { t } = useTranslation("auth");

  // #976 round 2 — this screen fills the whole viewport in --surface-2, with
  // no sidebar at all, so the meta colour is wrong at desktop width unless
  // resynced on both edges of this mount: entering here from the app shell,
  // and leaving here back to it (FarmThemeProvider sits outside the router
  // and cannot see either transition).
  useEffect(() => {
    resyncThemeColorMeta();
    return () => resyncThemeColorMeta();
  }, []);

  return (
    <Box
      component="main"
      data-meta-surface="auth"
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
          {nextStep ? (
            <Box sx={{
              display: { xs: "none", md: "block" }, marginTop: 3, padding: "14px",
              border: "1px solid var(--on-brand-mute)", borderRadius: "var(--r-panel)",
            }}>{nextStep}</Box>
          ) : (
            <Typography variant="body2" sx={{ color: "var(--on-brand-mute)", display: { xs: "none", md: "block" } }}>
              {t("shellTagline")}
            </Typography>
          )}
          {bannerSlot}
          <Typography variant="caption" sx={{ color: "var(--on-brand-mute)", marginTop: "auto", paddingTop: 3 }}>
            {footerNote}
          </Typography>
        </Box>
        <Box sx={{ flex: 1, padding: { xs: "20px", md: "32px" }, backgroundColor: "background.paper" }}>
          {nextStep && (
            <Box sx={{
              display: { xs: "block", md: "none" }, marginBottom: 2, padding: "14px",
              border: "1px solid var(--hairline)", borderRadius: "var(--r-panel)",
              background: "var(--tint-accent)", color: "var(--ink)",
            }}>{nextStep}</Box>
          )}
          {children}
        </Box>
      </Paper>
    </Box>
  );
}
