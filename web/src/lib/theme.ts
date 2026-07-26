// Light / night theme, user-controllable and persisted (#52). A same-origin
// pre-paint script (public/theme-init.js, loaded from index.html) resolves the
// theme before first paint and ALWAYS writes a concrete data-theme — the saved
// choice if there is one, otherwise the OS preference (#149). This module is
// the runtime source of truth for the toggle, and only ever reads the attribute
// that script left behind.
export type Theme = "light" | "dark";

const KEY = "cluckwork.theme";

export function initialTheme(): Theme {
  const set = document.documentElement.dataset.theme;
  // "light" is unreachable-by-fallback in the app (theme-init.js is
  // render-blocking) but keeps jsdom and any non-browser host from throwing.
  return set === "dark" ? "dark" : "light";
}

export function applyTheme(theme: Theme): void {
  // Attribute first: the in-memory choice must apply even if nothing persists.
  document.documentElement.dataset.theme = theme;
  try {
    localStorage.setItem(KEY, theme);
  } catch {
    // Writes can fail while reads still succeed (quota exhaustion), which would
    // leave the PREVIOUS value in place — so the next load would restore a
    // theme the user just moved away from. Dropping the key instead falls back
    // to the OS seed, which is at worst neutral rather than actively wrong.
    try {
      localStorage.removeItem(KEY);
    } catch {
      // storage fully unavailable; the attribute above is all we can do
    }
  }
}
