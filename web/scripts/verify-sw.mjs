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

import { readFileSync, existsSync, readdirSync } from "node:fs";
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

// 2. No runtime caching route may exist at all. `runtimeCaching: []` means
//    workbox registers no fetch handler beyond the precache + navigation route,
//    so every API call goes straight to the network.
const routeRegistrations = [...sw.matchAll(/\bregisterRoute\(/g)].length;
const navigationRegistrations = [
  ...sw.matchAll(/\bregisterRoute\(\s*new\s+[$\w]+\.NavigationRoute\(/g),
].length;
check(routeRegistrations === 1 && navigationRegistrations === 1,
  "unexpected Workbox route registration — API responses could be served from cache");

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

if (failures.length) {
  for (const f of failures) console.error(`::error::[service worker] ${f}`);
  console.error(`\n${failures.length} service-worker guarantee(s) broken in ${swPath}.`);
  process.exit(1);
}

console.log(
  `[service worker] ${swPath}: /api and /health excluded from the navigation fallback, ` +
  `no runtime caching strategy, ${precached.length} shell entries precached, ` +
  `${emittedJs.length} JavaScript assets verified and no API path among them.`,
);
