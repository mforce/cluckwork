// Light / night theme, user-controllable and persisted (#52). A same-origin
// pre-paint script (public/theme-init.js, loaded from index.html) resolves the
// theme before first paint and ALWAYS writes a concrete data-theme — the saved
// choice if there is one, otherwise the OS preference (#149). This module is
// the runtime source of truth for the toggle. watchDeviceTheme (#976 round 1)
// keeps following the OS preference live, past that first paint, while
// nothing is explicitly saved.
import { useSyncExternalStore } from "react";

export type Theme = "light" | "dark";

const KEY = "cluckwork.theme";

export function initialTheme(): Theme {
  const set = document.documentElement.dataset.theme;
  // "light" is unreachable-by-fallback in the app (theme-init.js is
  // render-blocking) but keeps jsdom and any non-browser host from throwing.
  return set === "dark" ? "dark" : "light";
}

// #976 round 1 — both writers below (a click, an OS change) go through this,
// so `useThemeMode` can notify its subscribers SYNCHRONOUSLY rather than
// waiting on a MutationObserver microtask: `ThemeToggle`'s own pre-existing
// tests click then assert the flipped label in the same tick.
const listeners = new Set<() => void>();
function setThemeAttribute(theme: Theme): void {
  document.documentElement.dataset.theme = theme;
  for (const listener of listeners) listener();
}

export function applyTheme(theme: Theme): void {
  // Attribute first: the in-memory choice must apply even if nothing persists.
  setThemeAttribute(theme);
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

/**
 * Follow-device mode must react to a LIVE OS scheme change, not only the
 * load-time resolution theme-init.js does. Writes the attribute directly
 * rather than through applyTheme, which would also PERSIST the value: an
 * OS-driven update stays non-sticky, exactly like the load-time resolution
 * it mirrors. Only while nothing is explicitly saved — an explicit choice
 * always wins over a later OS change. Returns the unsubscribe; called from
 * FarmThemeProvider's mount effect, which already owns this app's
 * `data-theme` observer lifecycle for MUI's own theme.
 */
export function watchDeviceTheme(): () => void {
  // jsdom has no matchMedia (BottomNav.tsx guards the same way).
  if (typeof window.matchMedia !== "function") return () => {};
  const media = window.matchMedia("(prefers-color-scheme: dark)");
  const onChange = () => {
    let saved: string | null = null;
    try {
      saved = localStorage.getItem(KEY);
    } catch {
      // storage unavailable — treat as "nothing saved", same as theme-init.js
    }
    if (saved === "light" || saved === "dark") return;
    setThemeAttribute(media.matches ? "dark" : "light");
  };
  media.addEventListener("change", onChange);
  return () => media.removeEventListener("change", onChange);
}

function subscribeToThemeMode(onChange: () => void): () => void {
  listeners.add(onChange);
  return () => listeners.delete(onChange);
}

/**
 * The resolved theme, live: `watchDeviceTheme` above now changes the
 * attribute without React knowing, so a component that read it once via
 * `useState(initialTheme)` goes stale — exactly the "button still says night
 * while the page is light" bug class #149 already named, from a new
 * trigger. `ThemeToggle` uses this instead of a local `useState`.
 */
export function useThemeMode(): Theme {
  return useSyncExternalStore(subscribeToThemeMode, initialTheme);
}
