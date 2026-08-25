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

// Second axis (#149), per farm since #586. Separate try — a failure reading the
// brand must not cost the theme resolved above.
//
// Four sources, first match wins. The farm is resolved from what exists BEFORE
// any token does: the URL, then the device's remembered codes (#535).
//   1. ?farm=<slug>          the branded-URL case. NO legacy fallback: a farm
//                            this device has no record of must not inherit
//                            whichever farm last painted here.
//   2. exactly one remembered  this device's farm. Falls back to the legacy
//                            un-namespaced key on a miss, which is what makes
//                            upgrade day flash-free — no build before #586 ever
//                            wrote a per-slug key.
//   3. NO roster key at all   no successful login since #535, so the legacy
//                            value belongs to this device's one farm. "[]" is
//                            NOT this case: removeFarmCode (#587) writes it, so
//                            an emptied roster on a multi-farm device would
//                            paint the wrong farm.
//   4. otherwise             several farms and none named. At /login the app
//                            does not know which farm, so it asserts none.
//
// Deliberately NO brand allowlist (unchanged): an unknown or future id matches
// no CSS rule and renders the default, exactly as no cache would. Duplicating
// the palette list here would instead mean a newly added palette silently loses
// its pre-paint cache and flashes aubergine.
//
// The slug pattern is an independent COPY of FARM_CODE_PATTERN in
// src/auth/farmCodeCache.ts — this file cannot import. Kept honest by the parity
// test in src/lib/themeInit.test.ts, which reads both files and compares them.
try {
  var slugPattern = /^[a-z0-9][a-z0-9-]{1,30}[a-z0-9]$/;
  var canon = function (value) {
    if (typeof value !== "string") return null;
    var normalized = value.trim().toLowerCase();
    return slugPattern.test(normalized) ? normalized : null;
  };
  var b = null;
  var urlSlug = canon(new URLSearchParams(location.search).get("farm"));
  if (urlSlug !== null) {
    b = localStorage.getItem("cluckwork.brand:" + urlSlug);
  } else {
    var rawRoster = localStorage.getItem("cluckwork.farmCodes");
    var roster = null;
    if (rawRoster !== null) {
      try {
        roster = JSON.parse(rawRoster);
      } catch (e) {
        roster = null;
      }
    }
    var single = Array.isArray(roster) && roster.length === 1 ? canon(roster[0]) : null;
    if (single !== null) {
      b = localStorage.getItem("cluckwork.brand:" + single);
      if (b === null) b = localStorage.getItem("cluckwork.brand");
    } else if (rawRoster === null) {
      b = localStorage.getItem("cluckwork.brand");
    }
  }
  if (b && b !== "aubergine") document.documentElement.dataset.brand = b;
} catch (e) {
  // storage unavailable — the API applies the brand after /account loads
}
