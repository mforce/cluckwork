// The sidebar/tab-bar switch (AppLayout.tsx, BottomNav.tsx) happens at MUI's
// default `md` breakpoint, 900px — every `sx={{ display: { xs: …, md: … } }}`
// site already reads that key. Two call sites need the SAME boundary as a
// plain media-query STRING rather than an sx breakpoint key: a JS
// `matchMedia` listener and a `useMediaQuery` hook. One constant so neither
// can drift from the CSS switch or from each other (PR #883 round 2, finding
// 2: BottomNav's own `matchMedia` listener read 901px against the 900px CSS
// switch — a sheet opened at exactly 900px would have stayed open with its
// trigger hidden underneath the now-visible sidebar).
export const MD_UP_QUERY = "(min-width: 900px)";
