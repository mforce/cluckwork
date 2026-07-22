// Light / night theme, user-controllable and persisted (#52). An inline script
// in index.html applies the saved choice before first paint; this module is the
// runtime source of truth for the toggle. With no explicit choice the app
// follows the OS setting (CSS `prefers-color-scheme`), so `initialTheme` only
// resolves a concrete value for the toggle's own state.
export type Theme = "light" | "dark";

const KEY = "cluckwork.theme";

export function initialTheme(): Theme {
  const set = document.documentElement.dataset.theme;
  if (set === "light" || set === "dark") return set;
  // jsdom has no matchMedia — guard so tests (and any non-browser host) don't throw.
  const prefersDark = typeof window.matchMedia === "function"
    && window.matchMedia("(prefers-color-scheme: dark)").matches;
  return prefersDark ? "dark" : "light";
}

export function applyTheme(theme: Theme): void {
  document.documentElement.dataset.theme = theme;
  try {
    localStorage.setItem(KEY, theme);
  } catch {
    // storage may be unavailable (private mode); the in-memory dataset still applies
  }
}
