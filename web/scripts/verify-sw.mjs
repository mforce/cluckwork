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
 * Pulls every runtime-caching route out of the generated worker — a
 * `registerRoute(<regex>, new $wb.<Strategy>(...), "GET")` call, as opposed to
 * the navigation route's `registerRoute(new $wb.NavigationRoute(...))`.
 */
function extractRuntimeCacheRoutes(source) {
  const routes = [];
  const marker = "registerRoute(";
  let i = 0;
  while (true) {
    const at = source.indexOf(marker, i);
    if (at < 0) break;
    i = at + marker.length;
    if (source[i] !== "/") continue; // navigation route, or something unrecognized
    const read = readRegexAt(source, i);
    if (!read) continue;
    const strategy = /^\s*,\s*new\s+[$\w]+\.(\w+)\(/.exec(source.slice(read.next, read.next + 200));
    routes.push({ regex: read.regex, strategy: strategy ? strategy[1] : null });
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
//    from the precache (check 5). It must be narrowly scoped — provably
//    unable to answer an /api or /health request — and use CacheFirst, so a
//    future widening of runtimeCaching cannot silently start serving stale
//    per-tenant data from cache the way an empty array once guaranteed it
//    couldn't.
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
    `runtime caching route ${route.regex} uses ${route.strategy ?? "an unrecognized"} strategy, not CacheFirst`);
  for (const path of ["/api", "/api/v1/flocks", "/api?x=1", "/API/v1/flocks", "/health/live", "/health"])
    check(!route.regex.test(path), `runtime caching route ${route.regex} would answer ${path} from cache`);
  for (const subset of ["cyrillic", "cyrillic-ext", "greek", "greek-ext", "vietnamese"])
    check(route.regex.test(`/assets/inter-${subset}-opsz-normal-deadbeef.woff2`),
      `runtime caching route ${route.regex} does not cover the dropped Inter ${subset} subset`);
  for (const subset of ["latin", "latin-ext"])
    check(!route.regex.test(`/assets/inter-${subset}-opsz-normal-deadbeef.woff2`),
      `runtime caching route ${route.regex} also matches the already-precached Inter ${subset} subset`);
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
