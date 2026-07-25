import { useState } from "react";
import { Moon, Sun } from "lucide-react";
import { applyTheme, initialTheme, type Theme } from "../lib/theme";

// Light/night switch (#52). Shared by the sidebar and the login screen. Reads
// the resolved theme for its own label.
//
// It deliberately does NOT track live OS changes (#149). The pre-paint script
// always writes a concrete data-theme, so the old `!dataset.theme` guard could
// never fire again — dead code that would have left the page dark while this
// button still said "Switch to night mode". An OS flip mid-session now waits
// for a reload; first-visit and cross-visit OS respect are both unaffected.
export function ThemeToggle({
  className = "",
  showLabel = true,
  iconSize = 17,
}: { className?: string; showLabel?: boolean; iconSize?: number }) {
  const [theme, setTheme] = useState<Theme>(initialTheme);

  function toggle() {
    const next: Theme = theme === "dark" ? "light" : "dark";
    applyTheme(next);
    setTheme(next);
  }

  return (
    <button
      type="button"
      className={`link theme-toggle ${className}`.trim()}
      onClick={toggle}
      aria-label={theme === "dark" ? "Switch to light mode" : "Switch to night mode"}
    >
      {theme === "dark" ? <Sun size={iconSize} aria-hidden /> : <Moon size={iconSize} aria-hidden />}
      {showLabel && <span>{theme === "dark" ? "Light" : "Night"}</span>}
    </button>
  );
}
