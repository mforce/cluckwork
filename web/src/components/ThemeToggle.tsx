import { useTranslation } from "react-i18next";
import { Moon, Sun } from "lucide-react";
import { Button, IconButton } from "@mui/material";
import { applyTheme, useThemeMode, type Theme } from "../lib/theme";

// Light/night switch (#52). Shared by the sidebar, the More sheet and the
// login screen.
//
// #976 round 1 — follow-device mode now tracks a live OS scheme change
// (`watchDeviceTheme`, wired from FarmThemeProvider), so the label reads the
// resolved theme through `useThemeMode` rather than a `useState` read once at
// mount: a stale local copy is exactly the "button still says night while
// the page is light" bug #149 already named, just from a new trigger.
//
// D2 pair 19 (#829): `IconButton` where a caller renders it icon-only
// (`showLabel={false}`, the two auth screens), `Button variant="text"` where
// it carries a label (the sidebar and the More sheet).
export function ThemeToggle({
  className = "",
  showLabel = true,
  iconSize = 17,
}: { className?: string; showLabel?: boolean; iconSize?: number }) {
  const { t } = useTranslation("themeToggle");
  const theme = useThemeMode();

  function toggle() {
    const next: Theme = theme === "dark" ? "light" : "dark";
    applyTheme(next);
  }

  const label = theme === "dark" ? t("switchToLightMode") : t("switchToNightMode");
  const icon = theme === "dark" ? <Sun size={iconSize} aria-hidden /> : <Moon size={iconSize} aria-hidden />;

  if (!showLabel) {
    return (
      <IconButton
        className={className}
        onClick={toggle}
        aria-label={label}
        size="small"
        sx={{ minWidth: 44, minHeight: 44 }}
      >
        {icon}
      </IconButton>
    );
  }

  return (
    <Button
      className={className}
      variant="text"
      color="inherit"
      onClick={toggle}
      aria-label={label}
      startIcon={icon}
      sx={{ justifyContent: "flex-start" }}
    >
      {theme === "dark" ? t("light") : t("night")}
    </Button>
  );
}
