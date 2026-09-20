import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Moon, Sun } from "lucide-react";
import { Button, IconButton } from "@mui/material";
import { applyTheme, initialTheme, type Theme } from "../lib/theme";

// Light/night switch (#52). Shared by the sidebar, the More sheet and the
// login screen. Reads the resolved theme for its own label.
//
// It deliberately does NOT track live OS changes (#149). The pre-paint script
// always writes a concrete data-theme, so the old `!dataset.theme` guard could
// never fire again — dead code that would have left the page dark while this
// button still said "Switch to night mode". An OS flip mid-session now waits
// for a reload; first-visit and cross-visit OS respect are both unaffected.
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
  const [theme, setTheme] = useState<Theme>(initialTheme);

  function toggle() {
    const next: Theme = theme === "dark" ? "light" : "dark";
    applyTheme(next);
    setTheme(next);
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
