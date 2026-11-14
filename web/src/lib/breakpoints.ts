// The sidebar/tab-bar switch (AppLayout.tsx, BottomNav.tsx) happens at MUI's
// default `md` breakpoint, 900px — every `sx={{ display: { xs: …, md: … } }}`
// site already reads that key. Two call sites need the SAME boundary as a
// plain media-query STRING rather than an sx breakpoint key: a JS
// `matchMedia` listener and a `useMediaQuery` hook. One constant so neither
// can drift from the CSS switch or from each other (PR #883 round 2, finding
// 2: BottomNav's own `matchMedia` listener read 901px against the 900px CSS
// switch — a sheet opened at exactly 900px would have stayed open with its
// trigger hidden underneath the now-visible sidebar).
import { useMediaQuery } from "@mui/material";

export const MD_UP_QUERY = "(min-width: 900px)";

// The seam under the phone/desktop switches. jsdom 31 evaluates media
// queries against a frozen 1024x768 viewport (its CSS environment is an
// Object.freeze'd fixed value — "replacing a public property does not change
// the underlying environment"), so an `{ xs, md }` switch can no longer be
// exercised from tests by viewport: the tab bar is permanently `md: none`.
// usePhoneMode() answers the switch — true on a phone — or null when
// nothing can determine it (no matchMedia: jsdom 30 and other bare hosts).
// A null answer tells call sites to keep the sx media query, which is what
// every host did before jsdom 31; a determined one lets them set the literal
// display instead. Tests pick an answer with stubMatchMedia (test/matchMedia.ts).
export function usePhoneMode(): boolean | null {
  const mdUp = useMediaQuery(MD_UP_QUERY);
  if (typeof window.matchMedia !== "function") return null;
  return !mdUp;
}

// The one place that encodes what a null answer means for a display switch:
// keep the `{ xs, md }` media query (every pre-jsdom-31 host, and jsdom 30
// with it). A determined answer collapses the switch to a literal.
export function phoneModeDisplay(phoneMode: boolean | null, phone: string, desktop: string) {
  return phoneMode === null ? { xs: phone, md: desktop } : phoneMode ? phone : desktop;
}
