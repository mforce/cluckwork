// web/scripts/runtime-cache-match.mjs — #948 review round 2; #1149 added
// matchesLanguagePack.
//
// The Inter extended-subset runtime cache (vite.config.ts) used a bare RegExp
// `urlPattern`. Workbox tests a RegExp `urlPattern` against the request's
// FULL href, unanchored, so `/inter-greek-opsz-normal-<hash>.woff2$` matched
// `/api/v1/x/inter-greek-opsz-normal-deadbeef.woff2` too — a same-origin path
// that happens to end with the right suffix. A regex has no way to see
// `request.destination`, and anchoring on the origin is fragile (scheme,
// host and port all vary by deployment). A match CALLBACK can see all three
// signals workbox-build's own docs recommend for exactly this case.
//
// This is a plain, dependency-free module (not `.ts`) so BOTH sides can
// import the SAME function with no build step: `vite.config.ts` uses it as
// the real `urlPattern`, and a test imports it directly and calls it with
// mock `{ url, request, sameOrigin }` objects — real behavior, never a
// regex re-derived from the generated worker's source text.

/**
 * @param {{ url: URL; request: { destination?: string }; sameOrigin: boolean }} args
 * @returns {boolean}
 */
export function matchesInterExtendedSubsetFont({ url, request, sameOrigin }) {
  return sameOrigin
    && url.pathname.startsWith("/assets/")
    && url.pathname.endsWith(".woff2")
    && request.destination === "font";
}

/** #1149 — the runtime cache for language packs, shared with the page (pwa/registerServiceWorker.ts). */
export const LANGUAGE_PACK_CACHE = "language-packs";

/** #1149 — how many pack URLs the cache keeps: es and tl for this release and the previous one. */
export const LANGUAGE_PACK_MAX_ENTRIES = 4;

/**
 * #1149 — the es/tl catalog chunks Vite emits for i18n/index.ts's dynamic
 * imports. They stay out of the precache (`globIgnores` in vite.config.ts) and
 * are cached here on first use. Workbox serializes this function's own source
 * into sw.js, so the language list must stay a literal inside it.
 *
 * @param {{ url: URL; request: { destination?: string }; sameOrigin: boolean }} args
 * @returns {boolean}
 */
export function matchesLanguagePack({ url, request, sameOrigin }) {
  return sameOrigin
    && request.destination === "script"
    && /^\/assets\/(?:es|tl)-[\w-]+\.js$/.test(url.pathname);
}
