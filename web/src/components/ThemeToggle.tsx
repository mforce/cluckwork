import { useEffect, useState } from "react";
import { Moon, Sun } from "lucide-react";
import { applyTheme, initialTheme, type Theme } from "../lib/theme";

// Light/night switch (#52). Shared by the sidebar and the login screen. Reads
// the resolved theme for its own label; while the user hasn't made an explicit
// choice it also tracks live OS changes so the control never goes stale.
export function ThemeToggle({
  className = "",
  showLabel = true,
  iconSize = 17,
}: { className?: string; showLabel?: boolean; iconSize?: number }) {
  const [theme, setTheme] = useState<Theme>(initialTheme);

  useEffect(() => {
    if (typeof window.matchMedia !== "function") return; // jsdom / non-browser
    const mq = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = () => {
      // only follow the OS while no explicit choice is stored
      if (!document.documentElement.dataset.theme) setTheme(mq.matches ? "dark" : "light");
    };
    mq.addEventListener("change", onChange);
    return () => mq.removeEventListener("change", onChange);
  }, []);

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
