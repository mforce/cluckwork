// web/scripts/font-cache-match.mjs — #948 review round 2.
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
