// Resolve the theme before first paint so there is no light/dark flash.
// Kept as a same-origin EXTERNAL file (not inline in index.html) so the
// Content-Security-Policy can stay `script-src 'self'` with no hash or nonce to
// maintain (#144). Loaded render-blocking in <head>, so it still runs before
// the first paint.
//
// This ALWAYS writes a concrete data-theme (#149). CSS therefore needs exactly
// one dark construct instead of that plus a prefers-color-scheme twin — the
// pair had already drifted and shipped a real bug (#131/F134).
//
// The rule that makes it work: seeding writes the ATTRIBUTE, never the KEY.
// Only applyTheme() writes cluckwork.theme. So a user who has never toggled
// keeps following their OS across visits, while an explicit choice is sticky.
var t = null;
try {
  t = localStorage.getItem("cluckwork.theme");
} catch (e) {
  // storage unavailable (private mode / blocked cookies) — fall through to the OS
}
// Outside the try on purpose: a storage read that throws must not cost the
// OS-preference seed, or the promised "always concrete" attribute is a lie.
if (t !== "light" && t !== "dark") {
  t = window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches
    ? "dark"
    : "light";
}
document.documentElement.dataset.theme = t;
