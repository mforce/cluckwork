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

// The seam under the phone/desktop switches. jsdom provides no matchMedia,
// and jsdom 31 evaluates CSS media rules at a fixed 1024px viewport, so an
// `{ xs, md }` switch cannot be exercised from tests by viewport: the tab
// bar is always `md: none` there. usePhoneMode() answers the switch — true
// on a phone — or null when the host has no matchMedia. A null answer tells
// call sites to keep the sx media query; a determined one lets them set the
// literal display instead. Tests pick an answer with stubMatchMedia
// (test/matchMedia.ts).
export function usePhoneMode(): boolean | null {
  const mdUp = useMediaQuery(MD_UP_QUERY);
  if (typeof window.matchMedia !== "function") return null;
  return !mdUp;
}

// The one place that encodes what a null answer means for a display switch:
// keep the `{ xs, md }` media query. A determined answer collapses the
// switch to a literal.
export function phoneModeDisplay(phoneMode: boolean | null, phone: string, desktop: string) {
  return phoneMode === null ? { xs: phone, md: desktop } : phoneMode ? phone : desktop;
}
