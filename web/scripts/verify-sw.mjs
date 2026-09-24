#!/usr/bin/env node
// Asserts the guarantees of the GENERATED service worker (#142 review).
//
// Why this exists: the safety-critical promise of the PWA work — "the service
// worker never answers an /api request, and never caches one" — lives in
// vite.config.ts and is resolved by vite-plugin-pwa/workbox into dist/sw.js.
// Nothing else in CI reads that output. The unit tests mock
// navigator.serviceWorker entirely, and the .NET integration tests write a
// placeholder sw.js and only check its HTTP headers. So a future refactor that
// narrows the denylist regex or adds a broad runtimeCaching rule would ship with
// every test green, silently reopening a cross-tenant/stale-data risk.
//
// This runs against the real emitted worker, after `vite build`.
//
// Usage: node scripts/verify-sw.mjs [dist]

import { readFileSync, existsSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";

const dist = process.argv[2] ?? "dist";
const swPath = join(dist, "sw.js");

const failures = [];
const check = (ok, message) => { if (!ok) failures.push(message); };

/**
 * Reads one regex literal starting at `source[i] === "/"`.
 *
 * Tracks escapes and character classes, so the `/` inside `[/?]` does not end
 * the literal. Returns the compiled RegExp and the index just past it, or null.
 */
function readRegexAt(source, i) {
  let body = "";
  let inClass = false;
  let j = i + 1;
  for (; j < source.length; j++) {
    const c = source[j];
    if (c === "\\") { body += c + (source[++j] ?? ""); continue; }
    if (c === "[") inClass = true;
    else if (c === "]") inClass = false;
    else if (c === "/" && !inClass) break;
    body += c;
  }
  if (j >= source.length) return null; // unterminated
  let flags = "";
  while (j + 1 < source.length && /[gimsuy]/.test(source[j + 1])) flags += source[++j];
  try {
    return { regex: new RegExp(body, flags), next: j + 1 };
  } catch {
    return null;
  }
}

/**
 * Pulls the navigation denylist out of the generated worker.
 *
 * Walks to the array's real closing bracket instead of matching `\[([^\]]*)\]`,
 * which stops at the first `]` — and that `]` now belongs to a character class
 * inside the very first pattern.
 */
function extractDenylist(source) {
  const at = source.indexOf("denylist:[");
  if (at < 0) return null;
  const patterns = [];
  let i = at + "denylist:[".length;
  while (i < source.length) {
    const c = source[i];
    if (c === "]") return { patterns, raw: source.slice(at, i + 1) };
    if (c === "/") {
      const read = readRegexAt(source, i);
      if (!read) return { patterns, raw: source.slice(at, i) };
      patterns.push(read.regex);
      i = read.next;
      continue;
    }
    i++; // commas, whitespace
  }
  return { patterns, raw: source.slice(at) };
}

/**
 * Reads a balanced `open`/`close` span starting at `source[i] === open`,
 * tracking string literals so a stray `)` or `}` inside one does not end it
 * early. Returns the index just past the matching close, or null.
 */
function readBalancedAt(source, i, open, close) {
  let depth = 0;
  let quote = null;
  let escaped = false;
  for (let j = i; j < source.length; j++) {
    const c = source[j];
    if (quote !== null) {
      if (escaped) escaped = false;
      else if (c === "\\") escaped = true;
      else if (c === quote) quote = null;
      continue;
    }
    if (c === "\"" || c === "'" || c === "`") { quote = c; continue; }
    if (c === open) depth++;
    else if (c === close && --depth === 0) return j + 1;
  }
  return null;
}

/**
 * Pulls every runtime-caching route out of the generated worker: a
 * `registerRoute(<matcher>, new $wb.<Strategy>(...), "GET")` call, as opposed
 * to the navigation route's `registerRoute(new $wb.NavigationRoute(...))`.
 * `<matcher>` is either a RegExp literal (#948 round 1's shape — a route this
 * repo no longer allows, see check 2 below) or a match-callback `function`
 * expression (workbox-build serializes a function `urlPattern` via its own
 * source, minified variable names and all — see check 2 for what still
 * survives that).
 */
function extractRuntimeCacheRoutes(source) {
  const routes = [];
  const marker = "registerRoute(";
  let i = 0;
  while (true) {
    const at = source.indexOf(marker, i);
    if (at < 0) break;
    const j = at + marker.length;
    i = j;
    if (source[j] === "/") {
      const read = readRegexAt(source, j);
      if (!read) continue;
      const strategy = /^\s*,\s*new\s+[$\w]+\.(\w+)\(/.exec(source.slice(read.next, read.next + 200));
      routes.push({ kind: "regex", regex: read.regex, strategy: strategy ? strategy[1] : null });
      continue;
    }
    if (source.startsWith("function", j)) {
      let k = j + "function".length;
      while (/\s/.test(source[k] ?? "")) k++;
      if (source[k] !== "(") continue;
      const afterParams = readBalancedAt(source, k, "(", ")");
      if (afterParams === null) continue;
      let m = afterParams;
      while (/\s/.test(source[m] ?? "")) m++;
      if (source[m] !== "{") continue;
      const afterBody = readBalancedAt(source, m, "{", "}");
      if (afterBody === null) continue;
      const strategy = /^\s*,\s*new\s+[$\w]+\.(\w+)\(/.exec(source.slice(afterBody, afterBody + 200));
      routes.push({ kind: "function", source: source.slice(j, afterBody), strategy: strategy ? strategy[1] : null });
      continue;
    }
    // navigation route (`new $wb.NavigationRoute(...)`), or something unrecognized.
  }
  return routes;
}

function extractPrecacheUrls(source) {
  const marker = "precacheAndRoute(";
  const callAt = source.indexOf(marker);
  if (callAt < 0) return null;

  let start = callAt + marker.length;
  while (/\s/.test(source[start] ?? "")) start++;
  if (source[start] !== "[") return null;

  let depth = 0;
  let quote = null;
  let escaped = false;
  for (let i = start; i < source.length; i++) {
    const c = source[i];
    if (quote !== null) {
      if (escaped) escaped = false;
      else if (c === "\\") escaped = true;
      else if (c === quote) quote = null;
      continue;
    }
    if (c === "\"" || c === "'") { quote = c; continue; }
    if (c === "[") depth++;
    if (c === "]" && --depth === 0) {
      const raw = source.slice(start, i + 1);
      return [...raw.matchAll(/url:"([^"]+)"/g)].map(([, url]) => url);
    }
  }
  return null;
}

if (!existsSync(swPath)) {
  console.error(`::error::${swPath} not found — run \`vite build\` first.`);
  process.exit(2);
}
const sw = readFileSync(swPath, "utf8");

// 1. The navigation fallback must refuse the server's own namespaces. Workbox
//    matches the denylist against pathname+search, so the pattern has to cover a
//    bare /api, a query-only /api?x, and (ASP.NET routing is case-insensitive)
//    /API — not just /api/.
const denylist = extractDenylist(sw);
check(denylist !== null, "no navigateFallbackDenylist found in the generated worker");
if (denylist) {
  // The emitted regexes, rebuilt and tested for real rather than eyeballed.
  // Scanned rather than eval'd — no `new Function` on file contents, even our
  // own, in a script whose whole job is checking a security property.
  const regexes = denylist.patterns;
  check(regexes.length > 0, "no usable regex patterns in the navigation denylist");
  for (const path of ["/api", "/api/v1/flocks", "/api?x=1", "/API/v1/flocks", "/health/live", "/health"])
    check(regexes.some((r) => r.test(path)), `navigation to ${path} would be served the cached shell`);
  // Sanity check in the other direction: a real app route must still fall back,
  // otherwise an over-broad denylist would quietly disable offline navigation.
  for (const path of ["/daily-entry", "/", "/settings"])
    check(!regexes.some((r) => r.test(path)), `app route ${path} is wrongly excluded from the fallback`);
}

// 2. Exactly one runtime caching route may exist beyond the navigation route:
//    #948's CacheFirst cache for the five Inter subsets `globIgnores` drops
//    from the precache (check 5). #948 review round 2: a RegExp `urlPattern`
//    is tested against the request's full href, UNANCHORED, so round 1's
//    route also matched an unrelated same-origin path that merely ended in
//    the right suffix (e.g. an /api/ route echoing a font-like filename) —
//    and a regex has no way to see `request.destination` at all. The route
//    must now be a match-callback FUNCTION (`font-cache-match.mjs`,
//    independently unit-tested elsewhere in this file), so a regex-typed
//    route — however narrow — fails this check outright, and the function's
//    own source is checked for the specific guards that close the gap.
//
//    Never eval the extracted source (`extractRuntimeCacheRoutes` above only
//    scans it, same discipline as the denylist extraction) — so this reads
//    the function as TEXT. That text is minified: local parameter names
//    (`url`, `request`, `sameOrigin`) are freely renamed, but a destructured
//    parameter's PROPERTY NAME (`sameOrigin:` in `{sameOrigin:l}`) is not,
//    because it must still match the real key on Workbox's own
//    `{url, request, sameOrigin}` argument — nor are property/method names
//    reached through a local variable (`.pathname`, `.startsWith`, a string
//    literal like `"/assets/"`). Those are what the checks below key on.
const routeRegistrations = [...sw.matchAll(/\bregisterRoute\(/g)].length;
const navigationRegistrations = [
  ...sw.matchAll(/\bregisterRoute\(\s*new\s+[$\w]+\.NavigationRoute\(/g),
].length;
check(navigationRegistrations === 1, "expected exactly one navigation route registration");
const runtimeRoutes = extractRuntimeCacheRoutes(sw);
check(routeRegistrations === navigationRegistrations + runtimeRoutes.length,
  "found a registerRoute() call this script could not classify as navigation or runtime caching");
check(runtimeRoutes.length === 1,
  `expected exactly one runtime caching route (the Inter extended-subset font cache), found ${runtimeRoutes.length}`);
for (const route of runtimeRoutes) {
  check(route.strategy === "CacheFirst",
    `runtime caching route uses ${route.strategy ?? "an unrecognized"} strategy, not CacheFirst`);
  check(route.kind === "function",
    "runtime caching route is a RegExp, not a match-callback function — a RegExp urlPattern is tested against " +
    "the request's full href, unanchored, so it can match an unrelated same-origin path (e.g. an /api/ route " +
    "with a font-like suffix) and cannot see request.destination (#948 round 2)");
  if (route.kind !== "function") continue;
  const fn = route.source;
  check(fn.includes("url:"), "match-callback does not destructure `url` from its argument");
  check(fn.includes("request:"), "match-callback does not destructure `request` from its argument");
  check(fn.includes("sameOrigin:"),
    "match-callback does not destructure `sameOrigin` — it could match a cross-origin request");
  check(/\.pathname\.startsWith\(\s*["']\/assets\/["']\s*\)/.test(fn),
    "match-callback does not require the /assets/ pathname prefix — it could match an /api/ path with a font-like suffix");
  check(/\.pathname\.endsWith\(\s*["']\.woff2["']\s*\)/.test(fn),
    "match-callback does not require a .woff2 suffix");
  check(/\.destination\b/.test(fn) && /["']font["']/.test(fn),
    "match-callback does not check request.destination === \"font\"");
  check(!fn.includes("||"),
    "match-callback combines a check with || — every guard must be required (&&), none may be optional");
}

// 3. Nothing resembling an API URL may be in the precache manifest, and every
// emitted JavaScript asset must be listed by Workbox's actual precache call.
const extractedPrecache = extractPrecacheUrls(sw);
check(extractedPrecache !== null, "no usable precacheAndRoute array found in the generated worker");
const precached = extractedPrecache ?? [];
const normalizedPrecache = new Set(precached.map((url) => url.replace(/^\.?\//, "")));
const emittedJs = readdirSync(join(dist, "assets"), { withFileTypes: true })
  .filter((entry) => entry.isFile() && entry.name.endsWith(".js"))
  .map((entry) => `assets/${entry.name}`)
  .sort();
const missingJs = emittedJs.filter((asset) => !normalizedPrecache.has(asset));

const apiish = precached.filter((url) => /(^|\/)api(\/|$)/i.test(url));
check(apiish.length === 0, `API paths found in the precache manifest: ${apiish.join(", ")}`);
check(precached.length > 0, "precache manifest is empty — the app shell would not be cached at all");
check(missingJs.length === 0, `emitted JavaScript missing from precache: ${missingJs.join(", ")}`);

// 4. Total precache weight, against an explicit ceiling (#825). #674 measured
// +320 KiB (+24%) for MUI's provider plus a realistic component kit over the
// hand-rolled baseline — real weight on a PWA meant for phones in sheds. Byte
// sizes are the only precache measure that is environment-independent
// (docs/decisions/407's writing-a-guard rule on golden values applies here
// too: a timing-based guard would not be), so this is where enforcement
// belongs; script duration is measured separately, on the real stack, in
// tools/simulation/ui/specs/dashboard-script-duration.spec.ts.
//
// 1,900 KiB, measured against the branch this ceiling was set on (75388d2,
// 2026-09-22): actual precache there is 1,806.38 KiB, after #826 and #827
// retired NamedEntityPicker's and Dialog's hand-built code but before #828
// (retires the isolated `NumberField`, ~-18 KiB projected in the issue's
// 2026-09-16 "Numbers for the ceiling choice" comment — that figure covers
// NumberField alone, not the hand-rolled tooltip positioning #828 also
// retires).
//
// #835 has now landed and did NOT cost the ~119 KiB this comment projected.
// Adopting Inter's `opsz` axis wholesale measured 1,979.79 KiB, 79.79 over;
// the five subsets no locale renders were dropped from the precache instead
// (check 5 below, and `globIgnores` in vite.config.ts), landing at 1,848.93
// KiB, which is 11.96 BELOW the 1,860.89 KiB the branch carried before it.
// The fallback D7.2 named for this case, one display cut for `h1`/`h2` only,
// was unbuildable: #864 had already moved both variants to `Georgia, serif`.
// A slice that adds weight without retiring hand-built code names the reason
// in its PR body, as a convention; it does not change this check's verdict.
const PRECACHE_CEILING_KIB = 1900;
let precacheBytes = 0;
const missingOnDisk = [];
for (const url of precached) {
  const assetPath = join(dist, url.replace(/^\.?\//, ""));
  if (!existsSync(assetPath)) { missingOnDisk.push(url); continue; }
  precacheBytes += statSync(assetPath).size;
}
check(missingOnDisk.length === 0,
  `precache manifest names files absent from ${dist}: ${missingOnDisk.join(", ")}`);
const precacheKiB = precacheBytes / 1024;
check(precacheKiB <= PRECACHE_CEILING_KIB,
  `precache is ${precacheKiB.toFixed(2)} KiB, over the ${PRECACHE_CEILING_KIB} KiB ceiling ` +
  `(#825) by ${(precacheKiB - PRECACHE_CEILING_KIB).toFixed(2)} KiB. Retire hand-built code, ` +
  "or find equivalent savings elsewhere, to pass this check. Naming the reason in the PR body " +
  "is the separate documentation convention #825 records; it does not make this check pass.");

// 5. The app's own typeface must still be in the precache, on the face that
// carries the optical-size axis (#835). `globIgnores` in vite.config.ts drops
// the five Inter subsets no locale renders; widening it to the Latin ones, or
// reverting to the wght-only entry, would both pass check 4 by getting SMALLER
// — a saving that costs the app its typeface offline, or its display cut.
const interFaces = precached.filter((url) => /inter-[a-z-]+-(opsz|wght|standard)-normal/.test(url));
for (const subset of ["latin", "latin-ext"])
  check(interFaces.some((url) => new RegExp(`inter-${subset}-opsz-normal`).test(url)),
    `the Inter ${subset} opsz face is not precached — the app ships en/es/tl and renders both subsets`);
const nonVariable = interFaces.filter((url) => !/-opsz-normal/.test(url));
check(nonVariable.length === 0,
  `precached Inter faces without the opsz axis: ${nonVariable.join(", ")} — ` +
  "display text would render at the opsz 14 text cut (#835)");

if (failures.length) {
  for (const f of failures) console.error(`::error::[service worker] ${f}`);
  console.error(`\n${failures.length} service-worker guarantee(s) broken in ${swPath}.`);
  process.exit(1);
}

console.log(
  `[service worker] ${swPath}: /api and /health excluded from the navigation fallback, ` +
  `${runtimeRoutes.length} narrowly-scoped runtime caching route(s) (none able to answer /api or ` +
  `/health), ${precached.length} shell entries precached, ${emittedJs.length} JavaScript assets ` +
  "verified and no API path among them.",
);
console.log(
  `[precache budget] ${precacheKiB.toFixed(2)} KiB / ${PRECACHE_CEILING_KIB} KiB ceiling ` +
  `(${(PRECACHE_CEILING_KIB - precacheKiB).toFixed(2)} KiB headroom).`,
);
